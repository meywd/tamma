namespace Tamma.Api.Services.Analytics;

/// <summary>
/// Story 36-4 — the tenant-scoped <b>cost &amp; spend</b> read seam over the
/// Story 36-1 <c>analytics_usage_daily</c> fact table (populated by Story 36-2).
/// The cost/spend sibling of <see cref="ITenantAnalyticsService"/> (usage): it
/// reuses the same per-tenant data plane (<c>ITenantDbContextFactory</c>) and
/// the same <see cref="AnalyticsWindow"/> (forced-UTC, clamp), and adds the
/// BYOK-vs-platform split (<c>CostUsd</c> for BYOK, <c>PlatformBilledUsd</c> for
/// billable), month-to-date + linear-run-rate projection, a read-only
/// <c>BudgetConfig</c> join, and a trend vs the prior equivalent window.
///
/// <para><b>Reads pre-aggregated facts ONLY, through the tenant's own schema.</b>
/// It never reads the control-plane <c>platform_analytics_hourly</c> or another
/// tenant's schema, and <b>never recomputes a margin</b> — <c>PlatformBilledUsd</c>
/// is the single source of truth (Story 36-2 materialised it via Story 34-5's
/// markup engine). Its only persisted side effect is the deduped
/// <see cref="CostAnalyticsEvents.BudgetProjectedExceeded"/> DCB event.</para>
/// </summary>
public interface ICostAnalyticsService
{
    /// <summary>
    /// Tenant-scoped cost/spend analytics over the (already-clamped, UTC)
    /// <paramref name="window"/>: a daily BYOK/platform split series (optionally
    /// grouped by <c>provider</c>|<c>agent</c>), month-to-date + linear-run-rate
    /// projected platform-billed spend, a <c>BudgetConfig</c> join for
    /// budget-vs-actual, and a trend vs the prior equivalent window. Emits
    /// <see cref="CostAnalyticsEvents.BudgetProjectedExceeded"/> (best-effort,
    /// per-period deduped) when the projection crosses the budget.
    /// </summary>
    /// <param name="tenantId">The route tenant (already membership-gated).</param>
    /// <param name="window">Effective UTC window for the series + trend.</param>
    /// <param name="groupBy">
    /// <c>null</c> = one row per day; <see cref="AnalyticsDimension.Provider"/> /
    /// <see cref="AnalyticsDimension.Agent"/> = one row per <c>(day, group)</c>,
    /// the <c>NULL</c> dimension surfaced as <c>"unattributed"</c>. Only Provider
    /// and Agent are valid here (the endpoint rejects other dimensions with 400).
    /// </param>
    /// <param name="mode">Process <c>ITammaModeProvider</c> value, carried only as an event tag.</param>
    Task<CostAnalyticsResponse> GetCostAsync(
        Guid tenantId,
        AnalyticsWindow window,
        AnalyticsDimension? groupBy,
        string mode,
        CancellationToken ct);
}
