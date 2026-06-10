using Npgsql;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Tests.TestDoubles;

/// <summary>
/// Shared test double for <see cref="ITenantConnectionResolver"/> that
/// hands EVERY tenant the same caller-supplied <see cref="NpgsqlDataSource"/>.
///
/// <para>For container harnesses (e.g. the ConventionStore suite) whose
/// unit under test is row-tier logic inside one physical database — the
/// fake replaces the resolution step so the harness can point a
/// <c>TenantDbContextFactory</c> at a specific store without running the
/// full provisioning pipeline. Per-tenant isolation through this double
/// is enforced only by <c>TenantDbContext.TenantId</c> + the EF query
/// filter, not by the connection. Tests that exercise the REAL
/// resolution path (tenant routing, eviction, provisioning) must use the
/// production <c>LruPooledTenantConnectionResolver</c> via the API
/// fixtures instead.</para>
///
/// <para>The data source is externally owned — the caller disposes it.</para>
/// </summary>
internal sealed class FixedDataSourceTenantConnectionResolver : ITenantConnectionResolver
{
    private readonly NpgsqlDataSource _dataSource;

    public FixedDataSourceTenantConnectionResolver(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public ValueTask<NpgsqlDataSource> GetDataSourceAsync(
        Guid tenantId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_dataSource);

    public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
        Guid tenantId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_dataSource);

    public ValueTask<ITenantConnectionLease> LeaseAsync(
        Guid tenantId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<ITenantConnectionLease>(new NoopLease(tenantId, _dataSource));

    public ValueTask EvictAsync(
        Guid tenantId, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public TenantConnectionPoolStats GetStats() =>
        new(WarmPoolCount: 1,
            TotalPoolsOpenedSinceStartup: 1,
            TotalPoolsEvictedSinceStartup: 0);

    /// <summary>
    /// Trivial lease over the shared data source — no ref counting;
    /// disposal only blocks further <see cref="DataSource"/> reads.
    /// </summary>
    private sealed class NoopLease : ITenantConnectionLease
    {
        private readonly NpgsqlDataSource _dataSource;
        private int _disposed;

        public NoopLease(Guid tenantId, NpgsqlDataSource dataSource)
        {
            TenantId = tenantId;
            _dataSource = dataSource;
        }

        public Guid TenantId { get; }

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
