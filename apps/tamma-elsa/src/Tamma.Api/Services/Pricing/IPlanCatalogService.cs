namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-1 — read seam for the plan price-book catalog. All catalog reads go
/// through this service so callers always get a fully-resolved, immutable
/// <see cref="PlanSnapshot"/> (header + features + entitlements + prices) rather
/// than touching the entity rows directly. Read-only and never throws on a
/// missing plan — returns <c>null</c>. The catalog is platform-global in both
/// single-user and SaaS modes (no per-tenant override layer).
/// </summary>
public interface IPlanCatalogService
{
    /// <summary>The single <c>active</c> version for a slug, or <c>null</c> if none.</summary>
    Task<PlanSnapshot?> GetActiveBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>
    /// A specific plan version by id (active OR deprecated). A deprecated
    /// version's snapshot is frozen — this is the historical-reproducibility
    /// read for a tenant still assigned an older version.
    /// </summary>
    Task<PlanSnapshot?> GetByIdAsync(Guid planId, CancellationToken ct = default);

    /// <summary>
    /// The snapshot for the plan version assigned to a tenant. Resolves the
    /// tenant's <c>PlanId</c> shadow column then snapshots that exact version.
    /// Returns <c>null</c> when the tenant has no assigned plan or doesn't exist.
    /// </summary>
    Task<PlanSnapshot?> GetForTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Every <c>active</c> version across all slugs.</summary>
    Task<IReadOnlyList<PlanSnapshot>> ListActiveAsync(CancellationToken ct = default);

    /// <summary>
    /// The full version chain for a slug — the active version plus all
    /// deprecated versions, ordered by <c>Version</c> descending. Empty when
    /// the slug is unknown.
    /// </summary>
    Task<IReadOnlyList<PlanSnapshot>> GetVersionsBySlugAsync(string slug, CancellationToken ct = default);
}
