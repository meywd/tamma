using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Data.Pooling;

/// <summary>
/// Production <see cref="ITenantConnectionResolver"/> for Story 28-4.
/// Owns a process-wide LRU cache of warm <see cref="NpgsqlDataSource"/>
/// instances keyed by tenant id, bounded by
/// <see cref="TenantConnectionPoolOptions.MaxEntries"/> (default 500).
///
/// <para><b>Hot path (cache hit)</b>: a single
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryGetValue"/> +
/// <see cref="LinkedListNode{T}"/> reposition under a short critical
/// section against <see cref="_lruLock"/>. No allocations. Returns the
/// cached <see cref="NpgsqlDataSource"/> directly.</para>
///
/// <para><b>Cold path (cache miss)</b>: serialised on a per-tenant
/// <see cref="SemaphoreSlim"/> drawn from <see cref="_buildLocks"/> so a
/// thundering herd of concurrent first-time requests for the same
/// tenant builds the pool exactly once. Steps:
/// <list type="number">
///   <item><description>Re-check the cache (another thread may have
///     populated it while we waited on the semaphore).</description></item>
///   <item><description>Load the tenant row from
///     <see cref="ControlPlaneDbContext"/> (using the short-lived
///     row cache from <see cref="_tenantRowCache"/>) and validate
///     <c>Status</c>.</description></item>
///   <item><description>Decrypt
///     <c>tenants.EncryptedConnectionString</c> via
///     <see cref="IConnectionStringDecryptor"/>.</description></item>
///   <item><description>Layer the per-tenant pool settings from
///     <see cref="TenantConnectionPoolOptions"/> on top of the decrypted
///     connection string and build a fresh
///     <see cref="NpgsqlDataSource"/>.</description></item>
///   <item><description>Insert into the LRU. If full, evict the least-
///     recently-used pool and dispose its data source.</description></item>
/// </list>
/// </para>
///
/// <para><b>Eviction</b>: <see cref="EvictAsync"/> removes a tenant from
/// the cache. Npgsql's <c>NpgsqlDataSource.DisposeAsync()</c> waits for
/// in-flight <see cref="NpgsqlConnection"/>s to be returned before
/// tearing the pool down, which covers the realistic concurrency window
/// for short-lived request/response handlers. For long-running consumers
/// (SSE streams, hosted services that hold a data-source reference
/// across awaits) the resolver also offers <see cref="LeaseAsync"/>
/// returning a ref-counted <see cref="TenantConnectionHandle"/>; eviction
/// while a handle is open marks the entry pending-dispose and defers the
/// actual <c>NpgsqlDataSource.DisposeAsync()</c> until the final handle
/// releases (Story 28-4 AC4).</para>
///
/// <para><b>Per-tenant Elsa schema</b> (<see cref="GetElsaDataSourceAsync"/>)
/// currently mirrors the app data source — Story 28-5 wires the
/// dedicated Elsa pool when per-tenant Elsa databases ship.</para>
///
/// <para><b>Rotation-driven invalidation gap</b>: the user task asks
/// the resolver to subscribe to <c>TENANT.CONNECTION_STRING_ROTATED.SUCCESS</c>
/// events via an <c>IPlatformEventBus</c>. That bus does NOT exist in
/// the repository today (verified 2026-04-18 — no implementations or
/// references). Until it lands, the rotation flow has to call
/// <see cref="EvictAsync"/> directly from whichever handler updates
/// <c>tenants.EncryptedConnectionString</c> (Story 28-12). The
/// <see cref="EvictAsync"/> contract is already shaped to be the
/// bus-subscriber callback, so wiring is mechanical when the bus
/// arrives.</para>
/// </summary>
public sealed class LruPooledTenantConnectionResolver
    : ITenantConnectionResolver, IAdminPoolDiagnostics, IAsyncDisposable
{
    /// <summary>
    /// LRU node payload — the cached data source, the tenant id, and an
    /// optional master <see cref="TenantConnectionHandle"/> covering any
    /// outstanding ref-counted leases. The <see cref="_lru"/> linked list
    /// orders these from most- to least-recently used.
    ///
    /// <para>The master handle is lazily created on the first
    /// <see cref="LeaseAsync"/> call for the tenant; bare
    /// <see cref="GetDataSourceAsync"/> consumers don't allocate a handle
    /// (and don't gain ref-count protection — they rely on Npgsql's own
    /// draining for short-lived requests).</para>
    /// </summary>
    private sealed class CacheEntry
    {
        public required Guid TenantId { get; init; }
        public required NpgsqlDataSource DataSource { get; init; }

        // Lazily created on first LeaseAsync. The master starts with
        // ref count = 1 (representing "the cache still holds the
        // entry"); LeaseAsync acquires sibling handles by incrementing.
        // EvictAsync (or LRU eviction) calls MarkPendingDispose() on
        // the master + disposes the master to release the cache lease;
        // the underlying NpgsqlDataSource is torn down when the final
        // sibling lease releases (or immediately if none were taken).
        public TenantConnectionHandle? MasterHandle;
    }

    private readonly IDbContextFactory<ControlPlaneDbContext> _cpFactory;
    private readonly IConnectionStringDecryptor _decryptor;
    private readonly TenantConnectionPoolMetrics _metrics;
    private readonly TenantConnectionPoolOptions _options;
    private readonly ILogger<LruPooledTenantConnectionResolver> _logger;
    private readonly ITenantStatusProbe? _statusProbe;

    /// <summary>
    /// Story 30-8 — V2 routing seam. Consulted before the legacy
    /// <c>EncryptedConnectionString</c> + <see cref="IConnectionStringDecryptor"/>
    /// path on cold-miss. When the directory returns
    /// <see cref="TenantEndpointResolution.IsApplicable"/> = <c>true</c>,
    /// the resolver uses the V2 <c>DatabaseUrl</c> directly and skips the
    /// decrypt path entirely. When it returns
    /// <see cref="TenantEndpointResolution.NotApplicable"/> the resolver
    /// falls through to the pre-30-8 behaviour so existing tenants on the
    /// shadow-column legacy path keep working without any V2 wiring.
    ///
    /// <para>Defaults to <see cref="NullTenantEndpointDirectory.Instance"/>
    /// when the constructor argument is <c>null</c> (DI hasn't registered
    /// the V2 directory yet, e.g. tests / single-user / pre-30-3 SaaS).</para>
    /// </summary>
    private readonly ITenantEndpointDirectory _endpointDirectory;

    /// <summary>
    /// Hot-path lookup. Value is the <see cref="LinkedListNode{T}"/>
    /// inside <see cref="_lru"/> so cache-hit reposition runs in O(1).
    /// </summary>
    private readonly ConcurrentDictionary<Guid, LinkedListNode<CacheEntry>> _pools = new();

    /// <summary>
    /// LRU ordering. <c>First</c> = most recently used, <c>Last</c> =
    /// next eviction victim. Mutated only under <see cref="_lruLock"/>.
    /// </summary>
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly object _lruLock = new();

    /// <summary>
    /// Per-tenant build-time semaphores. Shared across the process so
    /// repeated cold misses for the same tenant from concurrent
    /// requests collapse to a single Npgsql build.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _buildLocks = new();

    /// <summary>
    /// Short-lived cache of resolved tenant rows so eviction storms
    /// don't hammer the control plane on repeated cold misses. Window
    /// is <see cref="TenantConnectionPoolOptions.TenantRowCacheSeconds"/>.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset ExpiresAt, ResolvedTenantRow Row)> _tenantRowCache = new();

    /// <summary>
    /// Round-2 H5 — tracks outstanding deferred-dispose tasks fired by
    /// <see cref="HandleFinalLeaseReleased"/>. <see cref="DisposeAsync"/>
    /// awaits this set with a bounded timeout so the resolver doesn't
    /// race process shutdown against an unfinished
    /// <c>NpgsqlDataSource.DisposeAsync</c>. Items are removed once they
    /// complete (success or failure) via the cleanup continuation.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Task> _pendingDisposes = new();

    /// <summary>
    /// Story 30-8 — short-lived negative cache for tenants whose V2
    /// provider call raised <see cref="TenantNotProvisionedException"/>
    /// (or whose V2 path threw a transient failure). Without this, a
    /// dead tenant would storm the V2 provider on every cold-miss and
    /// every retry until the next status flip. TTL is bounded by
    /// <see cref="TenantConnectionPoolOptions.NotProvisionedNegativeCacheSeconds"/>.
    /// Eviction is lazy (next probe checks the timestamp) — no
    /// background sweeper needed. Cleared via <see cref="EvictAsync"/>
    /// alongside the row cache.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset ExpiresAt, string? Status)> _notProvisionedCache = new();

    /// <summary>
    /// Round-2 M7 — per-tenant outstanding-lease counter so the cap can
    /// be enforced without locking the LRU. Increments inside
    /// <see cref="LeaseAsync"/> after the cap check; decrements when a
    /// sibling handle's <c>DisposeAsync</c> runs (via
    /// <see cref="OnLeaseAcquired"/> / <see cref="OnLeaseReleased"/>
    /// hooks plumbed through <see cref="TenantConnectionHandle"/>).
    /// Values are best-effort — they may briefly drift across the CAS
    /// in <c>Acquire</c>, but converge on every dispose.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, int> _outstandingLeases = new();

    private int _disposed; // 0 = alive, 1 = disposed

    public LruPooledTenantConnectionResolver(
        IDbContextFactory<ControlPlaneDbContext> cpFactory,
        IConnectionStringDecryptor decryptor,
        TenantConnectionPoolMetrics metrics,
        IOptions<TenantConnectionPoolOptions> options,
        ILogger<LruPooledTenantConnectionResolver>? logger = null,
        ITenantStatusProbe? statusProbe = null,
        ITenantEndpointDirectory? endpointDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(cpFactory);
        ArgumentNullException.ThrowIfNull(decryptor);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(options);

        _cpFactory = cpFactory;
        _decryptor = decryptor;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger ?? NullLogger<LruPooledTenantConnectionResolver>.Instance;
        _statusProbe = statusProbe;
        // Story 30-8 — null directory is the safe default. SaaS deployments
        // wire a real ITenantEndpointDirectory in DI (Tamma.Api's
        // V2TenantEndpointDirectory) which dispatches via
        // TenantProviderRegistry. When null, every cold miss takes the
        // legacy decrypt-from-EncryptedConnectionString path verbatim.
        _endpointDirectory = endpointDirectory ?? NullTenantEndpointDirectory.Instance;

        if (_options.MaxEntries <= 0)
        {
            throw new ArgumentException(
                $"TenantConnectionPool:MaxEntries must be > 0 (got {_options.MaxEntries}).",
                nameof(options));
        }
        if (_options.MaxPoolSize <= 0)
        {
            throw new ArgumentException(
                $"TenantConnectionPool:MaxPoolSize must be > 0 (got {_options.MaxPoolSize}).",
                nameof(options));
        }
    }

    public ValueTask<NpgsqlDataSource> GetDataSourceAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Fast path. ConcurrentDictionary.TryGetValue is lock-free.
        if (_pools.TryGetValue(tenantId, out var node))
        {
            // Story 28-8 H12 — before serving a cached pool, consult the
            // status cache. A cache HIT with a non-active value means an
            // admin endpoint or workflow flipped the tenant since we
            // last warmed the pool; falling through to the cold path
            // forces a fresh CP read which raises
            // TenantNotProvisionedException with the correct status.
            // We DO NOT poll CP on every hit — only when the status
            // probe explicitly reports a non-active value. Probe miss
            // (no cached entry) keeps the fast-path semantics intact.
            if (_statusProbe is not null
                && _statusProbe.TryGet(tenantId, out var cachedStatus)
                && !IsActiveStatus(cachedStatus))
            {
                // Evict-then-rebuild — synchronously sequence the
                // eviction before the cold CP read so the slow path's
                // double-check doesn't observe the stale entry.
                return new ValueTask<NpgsqlDataSource>(
                    EvictThenResolveAsync(tenantId, cancellationToken));
            }

            // Reposition the LRU under a short lock. We re-check the
            // dictionary after taking the lock because EvictAsync /
            // BuildOrEvict may have removed the entry while we were
            // racing here — in that case fall through to the slow path.
            lock (_lruLock)
            {
                if (_pools.TryGetValue(tenantId, out var current) && current == node)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    _metrics.RecordHit();
                    return ValueTask.FromResult(node.Value.DataSource);
                }
            }
        }

        return new ValueTask<NpgsqlDataSource>(
            ResolveSlowAsync(tenantId, cancellationToken));
    }

    /// <summary>
    /// H12 — sequenced evict-then-rebuild for the status-flip detection
    /// path. Runs the eviction first so <see cref="ResolveSlowAsync"/>'s
    /// double-check inside the build-lock can't observe the stale entry.
    /// </summary>
    private async Task<NpgsqlDataSource> EvictThenResolveAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        await EvictAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return await ResolveSlowAsync(tenantId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Mirror of <see cref="ResolveTenantRowAsync"/>'s active-status
    /// rule (Doc 04 §2.2). NULL is treated as 'active' so legacy rows
    /// without the shadow column populated keep working without a
    /// status backfill.
    /// </summary>
    private static bool IsActiveStatus(string? status) =>
        status is null
        || string.Equals(status, "active", StringComparison.OrdinalIgnoreCase);

    public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
        // Per-tenant Elsa schema currently shares the app data source.
        // Story 28-5 splits these once dedicated Elsa DBs land.
        => GetDataSourceAsync(tenantId, cancellationToken);

    /// <summary>
    /// Story 28-4 AC4 — acquire a ref-counted lease over the tenant's
    /// per-tenant data source. Use this for long-running consumers
    /// (SSE streams, hosted services, Elsa long-running activities)
    /// that hold the data-source reference across multiple awaits and
    /// could otherwise be yanked by a mid-stream
    /// <see cref="EvictAsync"/>.
    ///
    /// <para>Short-lived request/response handlers should keep using
    /// <see cref="GetDataSourceAsync"/> — it's cheaper and Npgsql's own
    /// connection draining covers the eviction race for that pattern.</para>
    ///
    /// <para>Disposal rules: always wrap the returned handle in
    /// <c>await using</c>. Once disposed, <see cref="TenantConnectionHandle.DataSource"/>
    /// throws <see cref="ObjectDisposedException"/> on access — do not
    /// cache the data source past handle disposal.</para>
    /// </summary>
    public async ValueTask<ITenantConnectionLease> LeaseAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Per-tenant lease cap (Round-2 M7). Check BEFORE attempting the
        // cold-build so a pathological consumer can't stampede pool
        // builds. Refresh after the cold-build below to handle the
        // narrow window where the count was at the cap but a sibling
        // released while we were here.
        var leaseCap = Math.Max(1, _options.MaxOutstandingLeases);
        var current = _outstandingLeases.TryGetValue(tenantId, out var c) ? c : 0;
        if (current >= leaseCap)
        {
            throw new TenantLeaseLimitExceededException(tenantId, leaseCap, current);
        }

        // Reuse the same fast/slow path as GetDataSourceAsync. The
        // public method handles the LRU reposition + cold-build path;
        // we then ensure the cache entry has a master handle and
        // mint a sibling.
        await GetDataSourceAsync(tenantId, cancellationToken).ConfigureAwait(false);

        // After the await above, the entry MUST be in the cache (modulo
        // a pathological race with eviction — handled by the loop).
        var maxAttempts = Math.Max(1, _options.LeaseRetryAttempts);
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_pools.TryGetValue(tenantId, out var node))
            {
                // Evicted between our build and our handle acquisition.
                // Re-build by recursing into the cold path. Add a small
                // backoff so we don't hot-loop the cold-build path on a
                // pathological eviction storm.
                if (attempt > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5 * attempt),
                        cancellationToken).ConfigureAwait(false);
                }
                await GetDataSourceAsync(tenantId, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            // Lazily create the master handle. Use Interlocked.CompareExchange
            // so two concurrent LeaseAsync callers don't race to create
            // two masters (only the first one wins; the second is GC'd).
            var entry = node.Value;
            var master = entry.MasterHandle;
            if (master is null)
            {
                var fresh = new TenantConnectionHandle(
                    tenantId,
                    entry.DataSource,
                    onDisposed: HandleFinalLeaseReleased);
                master = Interlocked.CompareExchange(ref entry.MasterHandle, fresh, null) ?? fresh;
                if (!ReferenceEquals(master, fresh))
                {
                    // Lost the race — discard our fresh handle. Its
                    // ref count is 1 with no callback target; explicit
                    // dispose to honour IAsyncDisposable semantics.
                    await fresh.DisposeAsync().ConfigureAwait(false);
                }
            }

            try
            {
                // Re-check the cap inside the loop so a leak that
                // bloomed since entry can still be refused.
                var live = _outstandingLeases.TryGetValue(tenantId, out var l) ? l : 0;
                if (live >= leaseCap)
                {
                    throw new TenantLeaseLimitExceededException(tenantId, leaseCap, live);
                }

                var sibling = master.Acquire();
                // Track the new outstanding lease and arrange for it to
                // be decremented on dispose.
                _outstandingLeases.AddOrUpdate(tenantId, 1, (_, v) => v + 1);
                return new TrackedTenantConnectionLease(this, tenantId, sibling);
            }
            catch (ObjectDisposedException)
            {
                // Master was being torn down concurrently. Re-loop.
                continue;
            }
        }

        // Suggest a back-off proportional to the worst-case schedule
        // we just exhausted — gives the caller a hint without leaking
        // the internal step.
        var retryAfter = 5 * maxAttempts;
        throw new TenantConnectionLeaseRaceException(tenantId, maxAttempts, retryAfter);
    }

    /// <summary>
    /// Decrement the per-tenant outstanding-lease counter. Called by
    /// <see cref="TrackedTenantConnectionLease.DisposeAsync"/>. Removes
    /// the dictionary entry once the count drops to zero so the
    /// dictionary doesn't accumulate one entry per ever-leased tenant.
    /// </summary>
    private void DecrementLeaseCount(Guid tenantId)
    {
        _outstandingLeases.AddOrUpdate(tenantId, 0, (_, v) => Math.Max(0, v - 1));
        // Best-effort cleanup — if we just dropped to zero, remove the
        // entry. A concurrent increment would re-add it which is fine.
        if (_outstandingLeases.TryGetValue(tenantId, out var live) && live <= 0)
        {
            _outstandingLeases.TryRemove(new KeyValuePair<Guid, int>(tenantId, live));
        }
    }

    /// <summary>
    /// Wrapper lease that lets the resolver hook into dispose so the
    /// per-tenant outstanding-lease counter (Round-2 M7) stays in sync.
    /// Forwards every <see cref="ITenantConnectionLease"/> member to the
    /// inner sibling produced by
    /// <see cref="TenantConnectionHandle.Acquire"/>.
    /// </summary>
    private sealed class TrackedTenantConnectionLease : ITenantConnectionLease
    {
        private readonly LruPooledTenantConnectionResolver _owner;
        private readonly Guid _tenantId;
        private readonly TenantConnectionHandle _inner;
        private int _disposed;

        public TrackedTenantConnectionLease(
            LruPooledTenantConnectionResolver owner,
            Guid tenantId,
            TenantConnectionHandle inner)
        {
            _owner = owner;
            _tenantId = tenantId;
            _inner = inner;
        }

        public Guid TenantId => _inner.TenantId;
        public NpgsqlDataSource DataSource => _inner.DataSource;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _owner.DecrementLeaseCount(_tenantId);
            }
        }
    }

    public async ValueTask EvictAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        CacheEntry? evicted = null;
        lock (_lruLock)
        {
            if (_pools.TryRemove(tenantId, out var node))
            {
                _lru.Remove(node);
                evicted = node.Value;
            }
        }

        // Drop the row cache too so the next miss re-reads CP — handles
        // the rotation case where the encrypted CS changed.
        _tenantRowCache.TryRemove(tenantId, out _);

        // Story 30-8 — drop the negative cache so a status flip
        // (provisioning → ready, suspended → ready) propagates on the
        // next request without waiting for the negative-cache TTL.
        _notProvisionedCache.TryRemove(tenantId, out _);

        if (evicted is not null)
        {
            _metrics.RecordEviction("explicit");
            _logger.LogInformation(
                "tenant.pool.evicted tenantId={TenantId} reason=explicit",
                tenantId);
            await DisposeEvictedEntryAsync(evicted).ConfigureAwait(false);
            // Round-2 M13 — opportunistically trim the per-tenant build
            // lock now that the pool is gone. The per-tenant lease
            // counter is also dropped.
            if (_buildLocks.TryGetValue(tenantId, out var sem))
                TryTrimBuildLock(tenantId, sem);
            _outstandingLeases.TryRemove(tenantId, out _);
        }
    }

    public TenantConnectionPoolStats GetStats() =>
        new(WarmPoolCount: (int)_metrics.WarmPoolCount,
            TotalPoolsOpenedSinceStartup: _metrics.OpenedTotal,
            TotalPoolsEvictedSinceStartup: _metrics.EvictedTotal);

    /// <summary>
    /// Round-2 H5 — bounded timeout we wait for outstanding deferred
    /// disposes during resolver shutdown. Long enough that a healthy
    /// Postgres can finish a few <c>NpgsqlDataSource.DisposeAsync</c>
    /// calls; short enough that a wedged backend doesn't block process
    /// teardown indefinitely. Made internal so unit tests can shorten
    /// it without touching options plumbing.
    /// </summary>
    internal TimeSpan ShutdownDeferredDisposeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        // Snapshot under the lock so we don't observe partially-evicted
        // state while disposing.
        List<CacheEntry> entries;
        lock (_lruLock)
        {
            entries = _lru.ToList();
            _lru.Clear();
            _pools.Clear();
        }

        foreach (var entry in entries)
        {
            // Use the same eviction path so any open ref-counted leases
            // (LeaseAsync) hold the data source open until they release.
            // Resolver shutdown is best-effort — outstanding handles
            // typically belong to background services that have already
            // received the shutdown signal.
            try
            {
                await DisposeEvictedEntryAsync(entry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "tenant.pool.dispose_failed during resolver shutdown for tenant {TenantId}",
                    entry.TenantId);
            }
        }

        // Round-2 H5 — drain any deferred disposes that
        // HandleFinalLeaseReleased fired before the resolver shut
        // down. Without this drain, NpgsqlDataSource.DisposeAsync may
        // race process teardown and leak Postgres backend slots.
        // Bounded timeout so a wedged Postgres can't block forever.
        var pending = _pendingDisposes.Values.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                using var cts = new CancellationTokenSource(ShutdownDeferredDisposeTimeout);
                var allDone = Task.WhenAll(pending);
                var completed = await Task.WhenAny(
                    allDone,
                    Task.Delay(Timeout.InfiniteTimeSpan, cts.Token))
                    .ConfigureAwait(false);
                if (completed != allDone)
                {
                    _logger.LogWarning(
                        "tenant.pool.shutdown_timeout pendingDeferredDisposes={Count} timeoutSeconds={Seconds}",
                        pending.Length,
                        (int)ShutdownDeferredDisposeTimeout.TotalSeconds);
                }
            }
            catch (Exception ex)
            {
                // Don't surface — shutdown is best-effort. Log + move on.
                _logger.LogWarning(
                    ex,
                    "tenant.pool.shutdown_drain_failed pendingDeferredDisposes={Count}",
                    pending.Length);
            }
        }

        foreach (var sem in _buildLocks.Values)
            sem.Dispose();
        _buildLocks.Clear();
        _outstandingLeases.Clear();
        _pendingDisposes.Clear();
        _notProvisionedCache.Clear();

        // Round-2 review M12: do NOT dispose <see cref="_metrics"/>.
        // <see cref="TenantConnectionPoolMetrics"/> is registered as a
        // singleton in DI; its lifetime belongs to the
        // <see cref="IServiceProvider"/>, not this resolver. The host
        // disposes the singleton when the container itself disposes,
        // so reaching across that boundary here would double-dispose
        // (today <c>Meter.Dispose</c> is idempotent, but the contract
        // is "DI owns the singleton") and break composition roots that
        // share the metrics instance with other consumers.
    }

    // ── IAdminPoolDiagnostics (Story 28-4 AC5) ────────────────────────

    public DetailedPoolStats GetDetailedStats()
    {
        var hits = _metrics.HitsTotal;
        var misses = _metrics.MissesTotal;
        var total = hits + misses;
        var ratio = total == 0 ? 0d : (double)hits / total;

        // Sum the per-tenant outstanding-lease counter (Round-2 M7).
        // Cheap — bounded by the warm pool count.
        var totalLeases = 0;
        foreach (var kv in _outstandingLeases)
            totalLeases += kv.Value;

        return new DetailedPoolStats(
            WarmPoolCount: (int)_metrics.WarmPoolCount,
            OpenedTotal: _metrics.OpenedTotal,
            EvictedTotal: _metrics.EvictedTotal,
            EvictedByLru: _metrics.EvictedByLruTotal,
            EvictedExplicit: _metrics.EvictedExplicitTotal,
            HitsTotal: hits,
            MissesTotal: misses,
            HitRatio: ratio,
            DeferredDisposeBacklog: _pendingDisposes.Count,
            MaxOutstandingLeases: Math.Max(1, _options.MaxOutstandingLeases),
            TotalOutstandingLeases: totalLeases,
            BuildLocksRetained: _buildLocks.Count);
    }

    public IReadOnlyList<WarmTenantEntry> ListWarmTenants(int limit)
    {
        // Clamp 1..1000 — the cache is bounded by MaxEntries (default
        // 500) but explicit clamps keep the surface area predictable
        // across deploys.
        if (limit < 1) limit = 1;
        if (limit > 1000) limit = 1000;

        // Snapshot under the LRU lock. The list ordering is MRU-first
        // because LinkedList is rebuilt to match every cache hit's
        // reposition (AddFirst on hit, Remove last on overflow).
        lock (_lruLock)
        {
            var result = new List<WarmTenantEntry>(Math.Min(limit, _lru.Count));
            foreach (var entry in _lru)
            {
                if (result.Count >= limit) break;
                // RefCount = 1 (cache lease) + N (outstanding handles).
                // Surface only N (outstanding) to keep the meaning
                // intuitive for operators — "0 means safe to evict
                // immediately, >0 means deferral required".
                var leases = entry.MasterHandle is null
                    ? 0
                    : Math.Max(0, entry.MasterHandle.RefCount - 1);
                result.Add(new WarmTenantEntry(entry.TenantId, leases));
            }
            return result;
        }
    }

    // ── private helpers ───────────────────────────────────────────────

    private async Task<NpgsqlDataSource> ResolveSlowAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        _metrics.RecordMiss();

        // Story 30-8 — short-circuit on negative cache before taking the
        // build lock. A pathological retry loop on a failed-provisioning
        // tenant would otherwise queue up on the per-tenant semaphore.
        if (TryGetCachedNotProvisioned(tenantId, out var negStatus))
            throw new TenantNotProvisionedException(tenantId, negStatus);

        var sem = _buildLocks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check: another thread may have built it while we
            // were waiting. The fast-path lookup also reposition the
            // LRU — keep that semantics by going through the public
            // method.
            if (_pools.TryGetValue(tenantId, out var existing))
            {
                lock (_lruLock)
                {
                    if (_pools.TryGetValue(tenantId, out var current) && current == existing)
                    {
                        _lru.Remove(existing);
                        _lru.AddFirst(existing);
                        // No hit/miss tally here — we already counted the
                        // miss, and the second-arriver flow is an
                        // implementation detail.
                        return existing.Value.DataSource;
                    }
                }
            }

            // Re-check the negative cache inside the lock — another
            // thread may have just populated it while we were waiting on
            // the semaphore.
            if (TryGetCachedNotProvisioned(tenantId, out var negStatus2))
                throw new TenantNotProvisionedException(tenantId, negStatus2);

            // Story 30-8 — V2 directory dispatch. When the directory
            // returns IsApplicable=true, use its connection string
            // directly and skip the legacy decrypt-from-shadow-column
            // path. When it returns NotApplicable (no provider_key, or
            // the registry doesn't recognise the key), fall through to
            // the legacy path so existing tenants keep working without
            // any V2 wiring. Failures on the V2 path are cached
            // (negatively) for a short window via
            // NotProvisionedNegativeCacheSeconds.
            var connectionString = await ResolveConnectionStringAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);
            var dataSource = BuildDataSource(tenantId, connectionString);

            var entry = new CacheEntry
            {
                TenantId = tenantId,
                DataSource = dataSource,
            };
            var node = new LinkedListNode<CacheEntry>(entry);

            CacheEntry? evicted = null;
            lock (_lruLock)
            {
                _pools[tenantId] = node;
                _lru.AddFirst(node);

                if (_lru.Count > _options.MaxEntries)
                {
                    var victim = _lru.Last;
                    if (victim is not null)
                    {
                        _lru.RemoveLast();
                        _pools.TryRemove(victim.Value.TenantId, out _);
                        evicted = victim.Value;
                    }
                }
            }

            _metrics.RecordOpened();
            _logger.LogInformation(
                "tenant.pool.created tenantId={TenantId} maxPoolSize={MaxPoolSize}",
                tenantId,
                _options.MaxPoolSize);

            if (evicted is not null)
            {
                _metrics.RecordEviction("lru");
                _logger.LogInformation(
                    "tenant.pool.evicted tenantId={TenantId} reason=lru",
                    evicted.TenantId);
                _tenantRowCache.TryRemove(evicted.TenantId, out _);
                await DisposeEvictedEntryAsync(evicted).ConfigureAwait(false);
                // Round-2 M13 — try to trim the build lock for the
                // tenant we just evicted. Drop the lease counter too.
                if (_buildLocks.TryGetValue(evicted.TenantId, out var evictedSem))
                    TryTrimBuildLock(evicted.TenantId, evictedSem);
                _outstandingLeases.TryRemove(evicted.TenantId, out _);
            }

            return dataSource;
        }
        finally
        {
            sem.Release();
            // Round-2 M13 — opportunistically trim the per-tenant build
            // lock once it's no longer guarding a live pool. Without
            // this, a long-lived process that touches many tenants
            // accumulates one SemaphoreSlim per distinct tenant id
            // forever (the dictionary was previously never trimmed).
            // Only remove when the semaphore is unowned AND the pool
            // it was guarding is gone (or hasn't been rebuilt yet).
            // BuildLocksRetained on DetailedPoolStats lets ops detect
            // future leaks.
            TryTrimBuildLock(tenantId, sem);
        }
    }

    /// <summary>
    /// Round-2 M13 — try to remove an idle per-tenant build lock from
    /// <see cref="_buildLocks"/>. Called after a cold-build releases
    /// the semaphore. Removal is conditional:
    /// <list type="bullet">
    ///   <item><description>The pool we just built must not still be in
    ///     <see cref="_pools"/> (eviction or LRU-overflow happened).</description></item>
    ///   <item><description>Another caller might already be waiting on
    ///     this semaphore — checked via
    ///     <see cref="SemaphoreSlim.CurrentCount"/>.</description></item>
    ///   <item><description>Removal must be exact: only remove the
    ///     specific instance we recorded, not a fresh one a parallel
    ///     caller may have just minted.</description></item>
    /// </list>
    /// Trimming is best-effort — a missed trim just means one extra
    /// semaphore lives until the next cold miss for that tenant.
    /// </summary>
    private void TryTrimBuildLock(Guid tenantId, SemaphoreSlim ours)
    {
        // If the pool is still cached, the lock might be needed for a
        // future eviction-then-rebuild. Don't trim.
        if (_pools.ContainsKey(tenantId)) return;

        // Another waiter would mean count < 1 — we just released, so a
        // free semaphore reads CurrentCount == 1.
        if (ours.CurrentCount != 1) return;

        // ConcurrentDictionary lacks an exact "remove if value matches"
        // for value-type collisions, but the (Key, Value) overload does
        // exactly that.
        if (_buildLocks.TryRemove(new KeyValuePair<Guid, SemaphoreSlim>(tenantId, ours)))
        {
            try { ours.Dispose(); }
            catch
            {
                // Disposed by parallel trim — swallow. The semaphore is
                // already gone from the dictionary either way.
            }
        }
    }

    private async Task<ResolvedTenantRow> ResolveTenantRowAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (_tenantRowCache.TryGetValue(tenantId, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Row;
        }

        await using var ctx = await _cpFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // EncryptedConnectionString + KekVersion + Status are EF shadow
        // properties on the Tenant entity (see ControlPlaneDbContext).
        // We read them via the metadata API and project to the row record
        // to keep the resolver agnostic to the entity surface.
        var tenant = await ctx.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .Select(t => new
            {
                t.Id,
                t.DeletedAt,
                Status = EF.Property<string?>(t, "Status"),
                Envelope = EF.Property<byte[]?>(t, "EncryptedConnectionString"),
                KekVersion = (int?)EF.Property<short>(t, "KekVersion"),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (tenant is null || tenant.DeletedAt is not null)
            throw new TenantNotFoundException(tenantId);

        // 'active' is the only status that yields connections per Doc 04
        // §2.2. Other statuses (provisioning, suspended, deleting, ...)
        // map to TenantNotProvisioned. NULL status is treated as
        // 'active' to keep dev/seed rows working without forcing a
        // status backfill.
        var status = tenant.Status;
        if (status is not null && !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            throw new TenantNotProvisionedException(tenantId, status);

        if (tenant.Envelope is null || tenant.Envelope.Length == 0)
            throw new TenantConnectionStringMissingException(tenantId);

        var row = new ResolvedTenantRow(tenant.Envelope, tenant.KekVersion);

        var ttl = TimeSpan.FromSeconds(Math.Max(1, _options.TenantRowCacheSeconds));
        _tenantRowCache[tenantId] = (DateTimeOffset.UtcNow.Add(ttl), row);
        return row;
    }

    private string DecryptOrThrow(Guid tenantId, ResolvedTenantRow row)
    {
        try
        {
            var plaintext = _decryptor.Decrypt(row.Envelope, row.KekVersion);
            if (string.IsNullOrWhiteSpace(plaintext))
                throw new TenantConnectionDecryptionException(tenantId);
            return plaintext;
        }
        catch (TenantConnectionDecryptionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Wrap WITHOUT including the envelope contents — the inner
            // exception's stack is OK, but we never log the bytes.
            _logger.LogWarning(
                "tenant.pool.build_failed tenantId={TenantId} stage=decrypt error={ErrorType}",
                tenantId,
                ex.GetType().Name);
            throw new TenantConnectionDecryptionException(tenantId, ex);
        }
    }

    /// <summary>
    /// Story 30-8 — single chokepoint for "give me the postgres
    /// connection string for this tenant". Tries the V2 directory
    /// first; on <see cref="TenantEndpointResolution.NotApplicable"/>
    /// falls through to the legacy <c>EncryptedConnectionString</c> +
    /// <see cref="IConnectionStringDecryptor"/> path. Negative-caches
    /// <see cref="TenantNotProvisionedException"/> so a half-provisioned
    /// tenant doesn't storm the V2 provider on every retry.
    ///
    /// <para>The two paths intentionally share the same downstream
    /// <see cref="BuildDataSource"/> + LRU insertion code — the only
    /// difference is the source of the connection string.</para>
    /// </summary>
    private async Task<string> ResolveConnectionStringAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        // V2 dispatch. Wrap the call so we can negative-cache transient
        // failures without polluting the per-tenant SemaphoreSlim with a
        // burst of retries.
        TenantEndpointResolution resolution;
        try
        {
            resolution = await _endpointDirectory
                .TryResolveAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TenantNotProvisionedException ex)
        {
            CacheNotProvisioned(tenantId, ex.Status);
            throw;
        }
        catch (TenantNotFoundException)
        {
            // Bubble up — this is a definitive control-plane miss. The
            // ResolveTenantRowAsync fallback below would also throw
            // TenantNotFoundException, so short-circuiting here keeps
            // the surface uniform.
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Don't negative-cache — the caller may be holding a
            // transient network failure on the provider's external API.
            // Log + fall through to the legacy path so the tenant has a
            // chance to come up if the legacy column is populated.
            _logger.LogWarning(
                ex,
                "tenant.pool.v2_directory_failed tenantId={TenantId} fallback=legacy",
                tenantId);
            resolution = TenantEndpointResolution.NotApplicable;
        }

        if (resolution.IsApplicable)
        {
            // V2 path — use the provider's connection string verbatim.
            // The provider is responsible for any password rotation /
            // pgbouncer endpoint switching; we just feed it into
            // NpgsqlDataSource.
            var dbUrl = resolution.DatabaseUrl!;
            _logger.LogInformation(
                "tenant.pool.resolved_via_v2 tenantId={TenantId} provider={ProviderKey}",
                tenantId,
                resolution.ProviderKey);
            return dbUrl;
        }

        // Legacy path — decrypt the shadow column. Identical to the
        // pre-30-8 behaviour. Preserved intact for existing tenants
        // that don't have provider_key set.
        var row = await ResolveTenantRowAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return DecryptOrThrow(tenantId, row);
    }

    /// <summary>
    /// Story 30-8 — try-pattern read against the negative cache.
    /// Returns <c>true</c> when the tenant has a non-expired
    /// "not-provisioned" entry; the caller throws
    /// <see cref="TenantNotProvisionedException"/> with the captured
    /// status. Lazy expiry — a stale entry is removed on observation.
    /// </summary>
    private bool TryGetCachedNotProvisioned(Guid tenantId, out string? status)
    {
        if (_options.NotProvisionedNegativeCacheSeconds <= 0)
        {
            status = null;
            return false;
        }
        if (_notProvisionedCache.TryGetValue(tenantId, out var entry))
        {
            if (entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                status = entry.Status;
                return true;
            }
            // Stale — drop opportunistically. Concurrent updates are
            // fine; the negative cache is best-effort, not authoritative.
            _notProvisionedCache.TryRemove(
                new KeyValuePair<Guid, (DateTimeOffset, string?)>(tenantId, entry));
        }
        status = null;
        return false;
    }

    /// <summary>
    /// Story 30-8 — record a "not-provisioned" outcome for the tenant.
    /// Re-evaluating provisioning state requires either the TTL to
    /// expire OR a deliberate <see cref="EvictAsync"/> (which the
    /// status invalidation listener calls on
    /// <c>TENANT.STATUS_CHANGED</c>).
    /// </summary>
    private void CacheNotProvisioned(Guid tenantId, string? status)
    {
        var ttlSeconds = _options.NotProvisionedNegativeCacheSeconds;
        if (ttlSeconds <= 0) return;
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(ttlSeconds);
        _notProvisionedCache[tenantId] = (expiresAt, status);
    }

    private NpgsqlDataSource BuildDataSource(Guid tenantId, string connectionString)
    {
        // Layer per-tenant pool settings on top of the decrypted CS so
        // operator-supplied connection strings can't accidentally leave
        // MaxPoolSize at the Npgsql default of 100. The application-name
        // tag is appended (not overwritten) so admin diagnostics in
        // pg_stat_activity can attribute backends to a specific tenant.
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = _options.MaxPoolSize,
            MinPoolSize = _options.MinPoolSize,
            ConnectionIdleLifetime = _options.ConnectionIdleLifetimeSeconds,
            Timeout = _options.ConnectTimeoutSeconds,
            CommandTimeout = _options.CommandTimeoutSeconds,
            KeepAlive = _options.KeepAliveSeconds,
            ApplicationName = $"tamma-api;tenant={tenantId:D}",
        };

        return NpgsqlDataSource.Create(builder);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(nameof(LruPooledTenantConnectionResolver));
    }

    /// <summary>
    /// Dispose path for an entry that has just been removed from the
    /// cache (either by <see cref="EvictAsync"/> or the LRU-overflow
    /// branch in <see cref="ResolveSlowAsync"/>). If a master handle
    /// exists, marks it pending-dispose and disposes it (releasing the
    /// implicit cache lease); the underlying <c>NpgsqlDataSource</c>
    /// is then torn down either immediately (no outstanding sibling
    /// leases) or once the last sibling releases (deferred-dispose
    /// path through <see cref="HandleFinalLeaseReleased"/>). When no
    /// master exists, the data source is disposed inline because no
    /// long-running consumer can be holding it open via
    /// <see cref="LeaseAsync"/>.
    /// </summary>
    private async Task DisposeEvictedEntryAsync(CacheEntry entry)
    {
        var master = entry.MasterHandle;
        if (master is null)
        {
            // No leases ever taken — short-lived request/response
            // consumers only. Npgsql's own draining covers in-flight
            // queries; just dispose the data source.
            try
            {
                await entry.DataSource.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "tenant.pool.dispose_failed for tenant {TenantId}",
                    entry.TenantId);
            }
            return;
        }

        // Master exists. Mark pending so the deferred-dispose callback
        // fires when the last sibling releases. The MarkPendingDispose
        // return value tells us how many leases were outstanding when
        // we marked — useful for the deferred-vs-immediate decision +
        // for ops logging.
        var outstanding = master.MarkPendingDispose();

        // Release the implicit cache lease. If outstanding > 0, the
        // master's ref count drops to outstanding (still > 0) — the
        // callback fires later. If outstanding == 0 (only the cache
        // lease itself), the callback fires synchronously (well,
        // through the sync ValueTask path) and we dispose the data
        // source right away.
        if (outstanding > 1)
        {
            _logger.LogInformation(
                "tenant.pool.dispose_deferred tenantId={TenantId} outstandingLeases={Leases}",
                entry.TenantId,
                outstanding - 1);
        }
        await master.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Callback wired into every master <see cref="TenantConnectionHandle"/>
    /// at creation. Fires when the final sibling lease releases AND the
    /// resolver has marked the entry pending-dispose. Disposes the
    /// underlying <c>NpgsqlDataSource</c> on a tracked background task
    /// so the lease-releasing thread doesn't block on Postgres I/O.
    ///
    /// <para>Round-2 H5 — every fired task is registered into
    /// <see cref="_pendingDisposes"/> so <see cref="DisposeAsync"/> can
    /// await the backlog before the process exits. The
    /// <see cref="DetailedPoolStats.DeferredDisposeBacklog"/> diagnostic
    /// surfaces the live count for ops.</para>
    /// </summary>
    private void HandleFinalLeaseReleased(TenantConnectionHandle handle)
    {
        // Capture the data source via the internal accessor — the
        // handle's public DataSource getter throws because the handle
        // is now disposed. Move the actual Postgres-I/O dispose onto a
        // tracked background task so the lease-release path stays
        // synchronous from the consumer's perspective.
        var ds = handle.UnsafeRawDataSource;
        var tenantId = handle.TenantId;

        // Use a unique key (handle reference) so two evictions for the
        // same tenant in quick succession don't overwrite each other in
        // the tracking dictionary.
        var key = Guid.NewGuid();
        var disposeTask = Task.Run(async () =>
        {
            try
            {
                await ds.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "tenant.pool.dispose_failed (deferred) for tenant {TenantId}",
                    tenantId);
            }
            finally
            {
                _pendingDisposes.TryRemove(key, out _);
            }
        });
        _pendingDisposes[key] = disposeTask;
    }

    private readonly record struct ResolvedTenantRow(byte[] Envelope, int? KekVersion);
}
