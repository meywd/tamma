using Npgsql;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Tests.TestDoubles;

/// <summary>
/// Shared test double for <see cref="ITenantConnectionResolver"/> whose
/// <see cref="EvictAsync"/> is a silent no-op (no recording). Used by
/// tests that need a satisfiable resolver dependency but do not assert
/// pool-eviction behaviour. Data-source / lease methods throw because
/// no test currently relies on them through this double.
///
/// <para>Consolidates the per-file <c>NoopResolver</c> /
/// <c>NoopConnectionResolver</c> stubs across
/// <c>QuickWinsRound2Tests</c>, <c>KekRotationAdvisoryLockTests</c>,
/// <c>KekRotationPostFixTests</c>, and <c>KekRotationRetryTests</c>
/// (PF-C4 cleanup).</para>
/// </summary>
internal sealed class NoopTenantConnectionResolver : ITenantConnectionResolver
{
    public ValueTask<NpgsqlDataSource> GetDataSourceAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException(
            "NoopTenantConnectionResolver does not implement GetDataSourceAsync — tests using it should not exercise the data-source path.");

    public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException(
            "NoopTenantConnectionResolver does not implement GetElsaDataSourceAsync — tests using it should not exercise the data-source path.");

    public ValueTask<ITenantConnectionLease> LeaseAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException(
            "NoopTenantConnectionResolver does not implement LeaseAsync — tests using it should not exercise the lease path.");

    public ValueTask EvictAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public TenantConnectionPoolStats GetStats() => new(0, 0, 0);
}
