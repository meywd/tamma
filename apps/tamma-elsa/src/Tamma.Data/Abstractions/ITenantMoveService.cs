namespace Tamma.Data.Abstractions;

/// <summary>
/// Unified-tenancy Phase 4 — moves a tenant's schema to another pool
/// database with a brief read-only window (parent plan decision 4):
/// draining → pg_dump -n t_&lt;hex&gt; → restore into target → re-point the
/// encrypted connection string → evict pools → drop source schema →
/// bookkeeping → active. Same-cluster moves (source Host:Port == target)
/// keep the role + password and swap only the Database; cross-cluster
/// moves create the role on the target cluster with a fresh password.
/// </summary>
public interface ITenantMoveService
{
    Task MoveAsync(Guid tenantId, Guid targetDatabaseId, CancellationToken ct = default);
}
