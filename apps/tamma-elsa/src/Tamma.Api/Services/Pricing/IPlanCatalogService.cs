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
    /// Story 34-2 (AC1) — the public catalog: every <c>active</c>, non-custom
    /// (<c>IsCustom == false</c>) version, ordered by slug. This is what the
    /// pricing / upgrade UI renders — deprecated, draft, and custom plans are
    /// excluded by construction so a bespoke enterprise plan can never leak into
    /// the public list.
    /// </summary>
    Task<IReadOnlyList<PlanSnapshot>> ListActivePublicAsync(CancellationToken ct = default);

    /// <summary>
    /// Story 34-2 (AC2) — the single <c>active</c>, non-custom version for a slug,
    /// or <c>null</c>. A custom plan's slug is never resolvable through this route
    /// (it is <c>IsCustom == true</c>), so the public read returns 404 for it.
    /// </summary>
    Task<PlanSnapshot?> GetActivePublicBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>
    /// Story 34-2 — the admin catalog list. Unlike <see cref="ListActiveAsync"/>
    /// this surfaces every status (active / deprecated / draft) and includes
    /// custom plans, filtered by the supplied <paramref name="filter"/>
    /// (status / isCustom / bound tenantId). Ordered by slug then version
    /// descending.
    /// </summary>
    Task<IReadOnlyList<PlanSnapshot>> ListAllForAdminAsync(
        PlanListFilter filter, CancellationToken ct = default);

    /// <summary>
    /// The full version chain for a slug — the active version plus all
    /// deprecated versions, ordered by <c>Version</c> descending. Empty when
    /// the slug is unknown.
    /// </summary>
    Task<IReadOnlyList<PlanSnapshot>> GetVersionsBySlugAsync(string slug, CancellationToken ct = default);
}

/// <summary>
/// Story 34-2 — server-side filter for the admin catalog list
/// (<see cref="IPlanCatalogService.ListAllForAdminAsync"/>). All fields are
/// optional (null ⇒ no filter on that dimension). <see cref="TenantId"/> filters
/// custom plans to those bound to that tenant (matched on the server-derived
/// <c>custom-{tenantId:N}-*</c> slug — see <see cref="CustomPlanSlug"/>).
/// </summary>
public sealed record PlanListFilter(
    string? Status = null,
    bool? IsCustom = null,
    Guid? TenantId = null);
