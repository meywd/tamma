using Npgsql;
using Tamma.Data.Abstractions;

namespace Tamma.Data;

/// <summary>
/// Transitional <see cref="ITenantConnectionResolver"/> that routes every
/// tenant to the same shared <see cref="NpgsqlDataSource"/>. Wave A.5
/// post-merge: the original Story 28-3 stub was removed from the tree;
/// this restored version keeps DI registration working (all callers that
/// depend on <see cref="ITenantConnectionResolver"/>, notably
/// <c>KekRotationCoordinator</c> and <c>LruPooledTenantConnectionResolver</c>
/// consumers) until Story 28-4's real per-tenant pool cache is wired
/// in production. Per-tenant isolation at this point is still enforced
/// by <see cref="TenantDbContext.TenantId"/> + the query filter, not by
/// the connection.
///
/// <para>The resolver owns a single <see cref="NpgsqlDataSource"/> it
/// disposes when itself disposed. The pool statistics reported are
/// trivially synthetic — <c>WarmPoolCount</c> is always 1.</para>
/// </summary>
public sealed class StubTenantConnectionResolver
    : ITenantConnectionResolver, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _owned;

    /// <summary>Wrap an externally-owned data source. The caller keeps
    /// disposal responsibility (production path — Npgsql DataSource is
    /// typically owned by the DI container).</summary>
    public StubTenantConnectionResolver(NpgsqlDataSource dataSource)
        : this(dataSource, owned: false)
    {
    }

    private StubTenantConnectionResolver(NpgsqlDataSource dataSource, bool owned)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _owned = owned;
    }

    /// <summary>Build from a connection string — the resolver owns the
    /// resulting data source and disposes it on <see cref="DisposeAsync"/>.</summary>
    public static StubTenantConnectionResolver Create(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException(
                "StubTenantConnectionResolver requires a non-empty connection string.",
                nameof(connectionString));
        return new StubTenantConnectionResolver(
            NpgsqlDataSource.Create(connectionString),
            owned: true);
    }

    public ValueTask<NpgsqlDataSource> GetDataSourceAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_dataSource);

    public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_dataSource);

    public ValueTask<ITenantConnectionLease> LeaseAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default) =>
        // Stub doesn't track ref counts (single shared data source) —
        // hand back a no-op lease that wraps the shared data source.
        // The real LRU resolver overrides this to return a proper
        // ref-counted handle.
        ValueTask.FromResult<ITenantConnectionLease>(
            new NoopLease(tenantId, _dataSource));

    public ValueTask EvictAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public TenantConnectionPoolStats GetStats() =>
        new(WarmPoolCount: 1,
            TotalPoolsOpenedSinceStartup: 1,
            TotalPoolsEvictedSinceStartup: 0);

    public ValueTask DisposeAsync() =>
        _owned ? _dataSource.DisposeAsync() : ValueTask.CompletedTask;

    /// <summary>
    /// Trivial <see cref="ITenantConnectionLease"/> for the stub
    /// resolver — wraps the shared data source without ref counting.
    /// Disposing the lease is a no-op because the shared data source's
    /// lifetime is owned by the resolver, not the lease.
    /// </summary>
    private sealed class NoopLease : ITenantConnectionLease
    {
        private int _disposed;
        public NoopLease(Guid tenantId, NpgsqlDataSource dataSource)
        {
            TenantId = tenantId;
            _dataSource = dataSource;
        }

        public Guid TenantId { get; }
        private readonly NpgsqlDataSource _dataSource;

        public NpgsqlDataSource DataSource
        {
            get
            {
                if (Volatile.Read(ref _disposed) == 1)
                    throw new ObjectDisposedException(nameof(NoopLease));
                return _dataSource;
            }
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }
}
