namespace Tamma.Data.Abstractions;

/// <summary>Outcome of a placement decision (unified-tenancy Phase 2).</summary>
public sealed record TenantPlacement(Guid DatabaseId, string SchemaName);

/// <summary>
/// Assigns a tenant to a <c>tenant_databases</c> pool row by plan tier
/// (plans.PlacementPolicy: shared pool member vs dedicated DB) and
/// stamps tenants.DatabaseId + tenants.SchemaName. Idempotent: an
/// already-placed tenant returns its existing placement unchanged.
///
/// <para>Lives in Tamma.Data.Abstractions (implementation:
/// <c>Tamma.Api/Services/Provisioning/TenantPlacementService</c>) so the
/// tenant-lifecycle activities in Tamma.Activities can resolve it without
/// a hard dependency on Tamma.Api — same layering as
/// <see cref="IPlatformEventPublisher"/> (Phase 2 Task 4).</para>
/// </summary>
public interface ITenantPlacementService
{
    Task<TenantPlacement> AssignAsync(Guid tenantId, CancellationToken ct = default);
}
