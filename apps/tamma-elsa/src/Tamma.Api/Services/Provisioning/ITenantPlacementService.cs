namespace Tamma.Api.Services.Provisioning;

/// <summary>Outcome of a placement decision (unified-tenancy Phase 2).</summary>
public sealed record TenantPlacement(Guid DatabaseId, string SchemaName);

/// <summary>
/// Assigns a tenant to a <c>tenant_databases</c> pool row by plan tier
/// (plans.PlacementPolicy: shared pool member vs dedicated DB) and
/// stamps tenants.DatabaseId + tenants.SchemaName. Idempotent: an
/// already-placed tenant returns its existing placement unchanged.
/// </summary>
public interface ITenantPlacementService
{
    Task<TenantPlacement> AssignAsync(Guid tenantId, CancellationToken ct = default);
}
