using Npgsql;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Tests.TestDoubles;

/// <summary>
/// TEST-ONLY <see cref="ITenantConnectionResolver"/> that routes every
/// tenant to the same shared <see cref="NpgsqlDataSource"/>.
///
/// <para>Unified-tenancy Phase 3 deleted the production
/// <c>Tamma.Data.StubTenantConnectionResolver</c> — tenant data goes
/// exclusively through <c>LruPooledTenantConnectionResolver</c> now. This
/// relocated copy exists solely so the ConventionStore test harnesses keep
/// compiling until the Phase 3 test-fixture migration (Task 5) moves them
/// onto provisioned tenants, at which point this class should be deleted.
/// Per-tenant isolation through this double is enforced only by
/// <c>TenantDbContext.TenantId</c> + the EF query filter, not by the
/// connection.</para>
///
/// <para>The resolver owns a single <see cref="NpgsqlDataSource"/> it
/// disposes when itself disposed. The pool statistics reported are
/// trivially synthetic — <c>WarmPoolCount</c> is always 1.</para>
/// </summary>
internal sealed class StubTenantConnectionResolver
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
