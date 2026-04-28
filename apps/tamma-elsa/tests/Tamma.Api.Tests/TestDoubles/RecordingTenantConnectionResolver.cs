using Npgsql;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Tests.TestDoubles;

/// <summary>
/// Shared test double for <see cref="ITenantConnectionResolver"/> that
/// records every <see cref="EvictAsync"/> call. The data-source / lease
/// methods are not exercised by the test suites that use this double —
/// they throw <see cref="NotImplementedException"/> so misuse fails fast.
///
/// <para>Consolidates the per-file <c>RecordingResolver</c> /
/// <c>RecordingConnectionResolver</c> stubs that the round-2 fix wave
/// scattered across <c>AdminTenantsTests</c>,
/// <c>AdminTenantsAuditAndNoteTests</c>, and
/// <c>KekRotationCoordinatorTests</c> (PF-C4 cleanup).</para>
///
/// <para>Exposes both <c>Evictions</c> and <c>Evicted</c> property names
/// so call sites in either convention compile unchanged.</para>
/// </summary>
internal sealed class RecordingTenantConnectionResolver : ITenantConnectionResolver
{
    private readonly object _lock = new();
    private readonly List<Guid> _evicted = new();

    /// <summary>Mutable list of recorded evictions (legacy "Evictions" naming).</summary>
    public List<Guid> Evictions => _evicted;

    /// <summary>Snapshot view of recorded evictions (thread-safe "Evicted" naming).</summary>
    public IReadOnlyList<Guid> Evicted
    {
        get
        {
            lock (_lock) return _evicted.ToArray();
        }
    }

    public ValueTask<NpgsqlDataSource> GetDataSourceAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException(
            "RecordingTenantConnectionResolver does not implement GetDataSourceAsync — tests using it should not exercise the data-source path.");

    public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException(
            "RecordingTenantConnectionResolver does not implement GetElsaDataSourceAsync — tests using it should not exercise the data-source path.");

    public ValueTask<ITenantConnectionLease> LeaseAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException(
            "RecordingTenantConnectionResolver does not implement LeaseAsync — tests using it should not exercise the lease path.");

    public ValueTask EvictAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        lock (_lock) _evicted.Add(tenantId);
        return ValueTask.CompletedTask;
    }

    public TenantConnectionPoolStats GetStats() => new(0, 0, 0);
}
