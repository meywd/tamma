using Tamma.Core.Enums;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-6 — the single read seam that turns a tenant's pinned plan
/// assignment into a concrete, closed <see cref="ResolvedEntitlements"/> map,
/// plus a pure non-enforcing <see cref="CheckHeadroom"/> calc. The sibling
/// Enforcement epic, Billing (Epic 35), and both dashboards all answer
/// "what is this tenant allowed?" from this one calculation so limits never
/// drift.
///
/// <para>Read-only and non-mutating: it never writes catalog / assignment
/// rows, never charges, never meters, and never blocks a workflow.</para>
/// </summary>
public interface IEntitlementService
{
    /// <summary>
    /// Resolve the complete entitlement set for a principal (cache-first).
    /// Every <see cref="EntitlementMetricKey"/> member is present in the
    /// returned map (missing catalog rows backfill the documented default).
    ///
    /// <para>Throws <see cref="Tamma.Core.TammaError"/>
    /// (<c>ENTITLEMENT.RESOLVE.NO_ASSIGNMENT</c>, severity High) when the
    /// principal has no active plan assignment — it NEVER returns an
    /// empty/plain set (mirrors the prompt/convention fail-loud contract).
    /// Throws <c>ENTITLEMENT.RESOLVE.CATALOG_UNAVAILABLE</c> when a pinned
    /// plan id no longer resolves to a catalog snapshot.</para>
    /// </summary>
    Task<ResolvedEntitlements> ResolveAsync(
        EntitlementPrincipal principal, CancellationToken ct = default);

    /// <summary>
    /// Pure, non-enforcing headroom calc shared by enforcement + dashboards.
    /// Unlimited (<c>LimitValue == null</c>) ⇒ <c>Remaining = null,
    /// IsOver = false</c> regardless of usage.
    /// </summary>
    EntitlementHeadroom CheckHeadroom(
        ResolvedEntitlements resolved, EntitlementMetricKey metric, long currentUsage);
}
