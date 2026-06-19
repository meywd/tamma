using Tamma.Api.Services.Pricing;

namespace Tamma.Api.Endpoints.Admin;

/// <summary>
/// Story 34-1 — read-only plan price-book endpoints under
/// <c>/api/admin/plans*</c>. Every endpoint here MUST be gated behind the
/// <c>PlatformOwnerAccess</c> policy at the wiring site: the catalog is
/// platform-GLOBAL (incl. BYOK-vs-platform pricing) in both single-user and
/// SaaS modes (no per-tenant override layer), so it is platform-scoped admin
/// work. <c>OwnerAccess</c> would let every personal-tenant owner read the
/// whole platform price book (Finding C1) — only platform admins may.
///
/// <para>Write (create/deprecate version) endpoints are explicitly deferred to
/// Story 34-2 — this story ships only the three reads plus the tested
/// <see cref="PlanVersionEditor"/> service + its DCB events.</para>
/// </summary>
public static class PlanCatalogEndpoints
{
    /// <summary>GET /api/admin/plans — list all active plan snapshots.</summary>
    public static async Task<IResult> ListActive(
        IPlanCatalogService catalog,
        CancellationToken ct)
    {
        var plans = await catalog.ListActiveAsync(ct);
        return Results.Ok(new { plans });
    }

    /// <summary>GET /api/admin/plans/{slug} — the active version for a slug.</summary>
    public static async Task<IResult> GetActiveBySlug(
        string slug,
        IPlanCatalogService catalog,
        CancellationToken ct)
    {
        var snapshot = await catalog.GetActiveBySlugAsync(slug, ct);
        return snapshot is null
            ? Results.NotFound(new { error = "plan_not_found", slug })
            : Results.Ok(snapshot);
    }

    /// <summary>
    /// GET /api/admin/plans/{slug}/versions — the active + deprecated version
    /// chain for a slug (descending by version). Unknown slug → 404.
    /// </summary>
    public static async Task<IResult> GetVersions(
        string slug,
        IPlanCatalogService catalog,
        CancellationToken ct)
    {
        var versions = await catalog.GetVersionsBySlugAsync(slug, ct);
        return versions.Count == 0
            ? Results.NotFound(new { error = "plan_not_found", slug })
            : Results.Ok(new { slug, versions });
    }
}
