using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.Pricing;
using Tamma.Data;

namespace Tamma.Api.Endpoints.Admin;

/// <summary>
/// Story 34-9 — the platform-owner PRICING DASHBOARD read surface under
/// <c>GET /api/admin/pricing/overview</c>. One read-only aggregation that powers
/// the admin Pricing dashboard: the plan catalog (every version + custom), each
/// plan version's live <c>active</c>-tenant assignment count, and a rollup of the
/// platform margin configuration.
///
/// <para><b>RBAC / leak defence (AC7/AC13 mirror of the 34-5 estimate-leak fix):</b>
/// gated <c>PlatformOwnerAccess</c> at the wiring site (Finding C1) — the price
/// book and margin policies are platform-GLOBAL in both single-user and SaaS
/// modes (no per-tenant override layer), and this surface reveals
/// platform-internal economics (list prices + the margin knobs
/// <c>MarkupMultiplier</c> / <c>FixedUsdPer1M</c>) that a TENANT caller must NEVER
/// see. A tenant role gets 403 — the tenant-facing <c>/api/pricing/*</c> surface
/// (34-5) only ever exposes the sell price, never cost/margin.</para>
///
/// <para><b>No new logic, no migration (AC13):</b> a pure read/projection over
/// existing entities (<c>plans</c>, <c>tenant_plan_assignments</c>,
/// <c>margin_policies</c>) via <see cref="IPlanCatalogService"/> +
/// <see cref="ControlPlaneDbContext"/>. It re-implements NO pricing / margin /
/// entitlement business logic (34-5 / 34-6 / 34-7 own that) and adds NO schema.</para>
/// </summary>
public static class AdminPricingDashboardEndpoints
{
    // ── GET /api/admin/pricing/overview ──
    public static async Task<IResult> GetOverview(
        IPlanCatalogService catalog,
        ControlPlaneDbContext db,
        CancellationToken ct)
    {
        // The full admin catalog — every status (active / deprecated / draft) and
        // custom plans (an all-null filter applies no per-dimension filter). The
        // public /api/pricing/plans surface is the active+non-custom subset; the
        // admin dashboard needs the whole picture.
        var plans = await catalog.ListAllForAdminAsync(new PlanListFilter(), ct);

        // Live active-tenant assignment counts, grouped by the version-pinned
        // PlanId (a tenant may sit on a now-deprecated version, so we count by
        // PlanId, never by "the current active version of the slug"). Pure
        // GroupBy/Count — no bare string[] in the EF predicate (C#13 guard).
        var counts = await db.TenantPlanAssignments
            .AsNoTracking()
            .Where(a => a.Status == "active")
            .GroupBy(a => a.PlanId)
            .Select(g => new PlanAssignmentCount(g.Key, g.Count()))
            .ToListAsync(ct);
        var countByPlanId = counts.ToDictionary(x => x.PlanId, x => x.Count);

        var planRows = plans
            .Select(p => new PlanOverviewRow(
                p.PlanId,
                p.Slug,
                p.DisplayName,
                p.Version,
                p.Status,
                p.IsCustom,
                p.BillingInterval,
                // The recurring subscription list price (admin-only surface, so
                // exposing it is intentional; a tenant never reaches this route).
                p.Prices.Count > 0 ? p.Prices[0].RecurringUsd : null,
                countByPlanId.TryGetValue(p.PlanId, out var c) ? c : 0))
            .ToList();

        // Margin config rollup — active policies only (superseded rows are
        // history). The global markup is the always-present safety-net policy.
        var activeMargins = await db.MarginPolicies
            .AsNoTracking()
            .Where(m => m.Status == "active")
            .Select(m => new MarginRollupRow(m.Scope, m.MarkupMultiplier, m.FixedUsdPer1M))
            .ToListAsync(ct);

        var global = activeMargins.FirstOrDefault(m => m.Scope == "global");
        var margins = new MarginSummary(
            ActivePolicyCount: activeMargins.Count,
            GlobalPolicyCount: activeMargins.Count(m => m.Scope == "global"),
            PlanScopedPolicyCount: activeMargins.Count(m => m.Scope == "plan"),
            ProviderScopedPolicyCount: activeMargins.Count(m => m.Scope == "provider"),
            GlobalMarkupMultiplier: global?.MarkupMultiplier,
            GlobalFixedUsdPer1M: global?.FixedUsdPer1M);

        var totals = new PricingOverviewTotals(
            ActivePlanCount: planRows.Count(r => r.Status == "active" && !r.IsCustom),
            CustomPlanCount: planRows.Count(r => r.IsCustom),
            DeprecatedPlanCount: planRows.Count(r => r.Status == "deprecated"),
            TotalActiveAssignments: counts.Sum(x => x.Count),
            PlansWithActiveAssignments: countByPlanId.Count);

        return Results.Ok(new PricingOverviewResponse(planRows, margins, totals));
    }

    /// <summary>Internal projection row for the assignment-count GroupBy.</summary>
    private sealed record PlanAssignmentCount(Guid PlanId, int Count);

    /// <summary>Internal projection row for the active margin-policy rollup.</summary>
    private sealed record MarginRollupRow(string Scope, decimal? MarkupMultiplier, decimal? FixedUsdPer1M);
}

/// <summary>
/// Story 34-9 — top-level shape of <c>GET /api/admin/pricing/overview</c>: the
/// per-plan catalog rows (with live assignment counts), the margin-config
/// rollup, and headline totals.
/// </summary>
public sealed record PricingOverviewResponse(
    IReadOnlyList<PlanOverviewRow> Plans,
    MarginSummary Margins,
    PricingOverviewTotals Totals);

/// <summary>
/// One plan version in the admin dashboard: header fields projected from the
/// <see cref="PlanSnapshot"/> plus the count of tenants currently on an
/// <c>active</c> assignment pinned to this exact <see cref="PlanId"/>.
/// </summary>
public sealed record PlanOverviewRow(
    Guid PlanId,
    string Slug,
    string DisplayName,
    int Version,
    string Status,
    bool IsCustom,
    string BillingInterval,
    decimal? RecurringUsd,
    int ActiveTenantCount);

/// <summary>
/// A rollup of the platform's active margin policies (platform-internal
/// economics — this DTO is only ever returned by the platform-owner surface).
/// </summary>
public sealed record MarginSummary(
    int ActivePolicyCount,
    int GlobalPolicyCount,
    int PlanScopedPolicyCount,
    int ProviderScopedPolicyCount,
    decimal? GlobalMarkupMultiplier,
    decimal? GlobalFixedUsdPer1M);

/// <summary>Headline counters for the top of the admin pricing dashboard.</summary>
public sealed record PricingOverviewTotals(
    int ActivePlanCount,
    int CustomPlanCount,
    int DeprecatedPlanCount,
    int TotalActiveAssignments,
    int PlansWithActiveAssignments);
