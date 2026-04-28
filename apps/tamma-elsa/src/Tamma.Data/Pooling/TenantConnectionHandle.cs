using Npgsql;
using Tamma.Data.Abstractions;

namespace Tamma.Data.Pooling;

/// <summary>
/// Ref-counted lease over a per-tenant <see cref="NpgsqlDataSource"/>
/// that prevents the underlying pool from being yanked while a
/// long-running consumer (SSE stream, hosted background loop, Elsa
/// long-running activity) still holds a reference. Story 28-4 AC4.
///
/// <para><b>Why this exists.</b> Short-lived request/response paths
/// rely on Npgsql's own connection draining: <c>NpgsqlDataSource.DisposeAsync</c>
/// blocks until in-flight <c>NpgsqlConnection</c>s are returned. That
/// covers the realistic concurrency window for HTTP request handlers.
/// Long-lived consumers that hold a data-source reference across
/// multiple awaits (SSE streams over <c>IAsyncEnumerable</c>, hosted
/// services that loop the same connection across ticks) are NOT
/// covered — the resolver could call
/// <see cref="ITenantConnectionResolver.EvictAsync"/> mid-stream and
/// dispose the data source out from under them. The handle wraps each
/// data-source acquisition with a ref count so eviction defers the
/// actual dispose until every outstanding handle is released.</para>
///
/// <para><b>Shared state.</b> Master + sibling handles share a single
/// <see cref="HandleState"/> object holding the ref count, the
/// pending-dispose flag, and the deferred-dispose callback. Each
/// individual handle contributes one ref to the count when constructed
/// and decrements it when disposed; the underlying data source is torn
/// down when the count drops to zero AND the pending-dispose flag is
/// set. The master handle is the one the resolver explicitly disposes
/// at eviction time (releasing the implicit "cache holds a lease" ref);
/// sibling handles are minted via <see cref="Acquire"/> for each new
/// consumer.</para>
///
/// <para><b>Usage contract</b>:
/// <list type="bullet">
///   <item><description>Acquire via
///     <see cref="ITenantConnectionResolver.LeaseAsync"/> (preferred for
///     SSE / streams / long-running scopes).</description></item>
///   <item><description>Always wrap in <c>await using</c> — disposal
///     decrements the ref count.</description></item>
///   <item><description>Treat <see cref="DataSource"/> as scoped to the
///     handle's lifetime; do NOT cache it past disposal.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class TenantConnectionHandle : ITenantConnectionLease
{
    /// <summary>
    /// Shared state across the master + every sibling handle of one
    /// cache entry. All ref-count + pending-dispose mutations target
    /// this shared instance via <see cref="Interlocked"/> primitives,
    /// so a sibling disposing decrements the same counter the master
    /// is reading.
    /// </summary>
    internal sealed class HandleState
    {
        public required Guid TenantId { get; init; }
        public required NpgsqlDataSource DataSource { get; init; }
        public required Action<HandleState>? OnDisposed { get; init; }

        // Total outstanding handles (master + siblings) referring to
        // this state. Bumped by every ctor; decremented by every
        // DisposeAsync. The OnDisposed callback fires when this drops
        // to zero AND PendingDispose has been set.
        public int RefCount;

        // 0 = active, 1 = pending dispose. Set by MarkPendingDispose.
        public int PendingDispose;
    }

    private readonly HandleState _state;
    private int _localDisposed;

    /// <summary>
    /// Construct the master handle for a fresh cache entry. Bumps the
    /// shared ref count to 1 (the implicit "cache holds a lease" ref).
    /// </summary>
    internal TenantConnectionHandle(
        Guid tenantId,
        NpgsqlDataSource dataSource,
        Action<TenantConnectionHandle>? onDisposed)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        // Adapt the per-handle callback into the state-level callback
        // so the resolver's HandleFinalLeaseReleased(handle) signature
        // stays unchanged.
        Action<HandleState>? stateCallback = onDisposed is null
            ? null
            : state => onDisposed(this);
        _state = new HandleState
        {
            TenantId = tenantId,
            DataSource = dataSource,
            OnDisposed = stateCallback,
        };
        Interlocked.Increment(ref _state.RefCount);
    }

    /// <summary>
    /// Construct a sibling handle bound to an existing
    /// <see cref="HandleState"/>. Increments the shared ref count by
    /// one — the caller (the master's <see cref="Acquire"/>) has
    /// already validated the count was &gt; 0 under a CAS so the
    /// sibling can't be born onto an already-disposed state.
    /// </summary>
    private TenantConnectionHandle(HandleState state)
    {
        _state = state;
        // Sibling already credited via Acquire's CAS — don't bump
        // again. (Acquire CAS-incremented the shared count then
        // handed the new ref count off to this handle.)
    }

    /// <inheritdoc />
    public Guid TenantId => _state.TenantId;

    /// <inheritdoc />
    public NpgsqlDataSource DataSource
    {
        get
        {
            if (Volatile.Read(ref _localDisposed) == 1)
                throw new ObjectDisposedException(nameof(TenantConnectionHandle));
            return _state.DataSource;
        }
    }

    /// <summary>
    /// Internal accessor for the wrapped <see cref="NpgsqlDataSource"/>
    /// that bypasses the disposed-check on
    /// <see cref="DataSource"/>. Used by the resolver's
    /// deferred-dispose callback because the handle is already disposed
    /// (its initial lease released) by the time the callback fires.
    /// External callers MUST use <see cref="DataSource"/> instead.
    /// </summary>
    internal NpgsqlDataSource UnsafeRawDataSource => _state.DataSource;

    /// <summary>
    /// True once <see cref="MarkPendingDispose"/> has been called on
    /// any handle that shares this state.
    /// </summary>
    public bool IsPendingDispose => Volatile.Read(ref _state.PendingDispose) == 1;

    /// <summary>
    /// Current outstanding lease count across master + siblings.
    /// Mainly for tests + admin diagnostics. Read with
    /// <see cref="Volatile.Read(ref int)"/> — the value can change
    /// between observation and the next operation.
    /// </summary>
    public int RefCount => Volatile.Read(ref _state.RefCount);

    /// <summary>
    /// Mint a sibling handle that shares the same data source ref
    /// count. Used by the resolver to hand out additional leases for
    /// the same underlying pool without re-running the cold-miss build
    /// path. Throws <see cref="ObjectDisposedException"/> if the shared
    /// state is already torn down.
    /// </summary>
    internal TenantConnectionHandle Acquire()
    {
        // CAS-loop: only increment if the shared count is still > 0.
        // A value of zero means the underlying data source has been
        // (or is being) disposed.
        while (true)
        {
            var current = Volatile.Read(ref _state.RefCount);
            if (current <= 0)
                throw new ObjectDisposedException(nameof(TenantConnectionHandle));
            if (Interlocked.CompareExchange(ref _state.RefCount, current + 1, current) == current)
                break;
        }
        return new TenantConnectionHandle(_state);
    }

    /// <summary>
    /// Signal that the resolver wants the underlying data source torn
    /// down once all outstanding leases release. Idempotent. Returns
    /// the post-mark ref count so the resolver can decide whether to
    /// await dispose immediately (count == 1, only the cache lease
    /// remains) or rely on the deferred path (count &gt; 1).
    /// </summary>
    internal int MarkPendingDispose()
    {
        Interlocked.Exchange(ref _state.PendingDispose, 1);
        return Volatile.Read(ref _state.RefCount);
    }

    /// <summary>
    /// Release this lease. When the final lease releases AND the
    /// shared state has been marked pending-dispose, invokes the
    /// resolver's dispose callback so the underlying data source is
    /// torn down. Idempotent under double dispose.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _localDisposed, 1) == 1)
            return ValueTask.CompletedTask;

        var remaining = Interlocked.Decrement(ref _state.RefCount);
        if (remaining == 0 && Volatile.Read(ref _state.PendingDispose) == 1)
        {
            _state.OnDisposed?.Invoke(_state);
        }
        return ValueTask.CompletedTask;
    }
}
