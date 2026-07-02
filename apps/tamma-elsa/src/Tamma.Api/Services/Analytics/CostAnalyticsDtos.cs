namespace Tamma.Api.Services.Analytics;

/// <summary>
/// Story 36-4 — request/response contract for the tenant-facing cost &amp; spend
/// analytics API (<c>GET /api/v1/orgs/{tenantId}/analytics/cost</c>).
///
/// <para>The <b>read</b> side over the Story 36-1 <c>analytics_usage_daily</c>
/// fact table (populated by Story 36-2). It separates the raw provider cost the
/// tenant paid on their own BYOK keys (<see cref="CostSeriesBucket.ByokCostUsd"/>
/// — informational, never marked up) from the platform-fronted <b>billable</b>
/// amount (<see cref="CostSeriesBucket.PlatformBilledUsd"/> — already
/// materialized upstream; this endpoint sums it, never recomputes it).</para>
///
/// <para><b>No margin leak:</b> a platform row's raw <c>CostUsd</c> (Tamma's
/// underlying provider cost — from which the margin <c>= billed − cost</c> could
/// be derived) is <b>never</b> summed into any response field. Only the tenant's
/// own BYOK cost and the tenant's own billed amount are exposed.</para>
///
/// <para>These types ride the app-wide camelCase JSON policy (Program.cs
/// <c>ConfigureHttpJsonOptions</c>); <see cref="DateOnly"/> serialises as
/// <c>yyyy-MM-dd</c> (System.Text.Json default), matching the daily grain.</para>
/// </summary>

/// <summary>The effective (clamped, UTC) window echoed back on the response.</summary>
public sealed record CostWindow(DateOnly From, DateOnly To);

/// <summary>
/// One daily bucket of the cost time-series. When ungrouped, <see cref="Group"/>
/// is <c>null</c> and there is one row per day. When grouped by provider/agent,
/// there is one row per <c>(day, group)</c> and the <c>NULL</c> dimension value
/// is surfaced as <c>"unattributed"</c> — never dropped, so per-group sums
/// reconcile to the ungrouped total.
/// </summary>
public sealed record CostSeriesBucket(
    DateOnly Day,
    string? Group,
    decimal ByokCostUsd,
    decimal PlatformBilledUsd);

/// <summary>
/// Cost/spend summary: the window's informational BYOK total, month-to-date +
/// linear-run-rate projected platform-billed spend, and the budget-vs-actual
/// context (all budget/utilisation fields are <c>null</c> when no
/// <c>BudgetConfig</c> row exists for the tenant).
/// </summary>
public sealed record CostSummary(
    decimal ByokCostUsd,
    decimal MtdPlatformBilledUsd,
    decimal ProjectedPlatformBilledUsd,
    decimal? BudgetUsd,
    double? AlertThreshold,
    double? BudgetUtilizationPct,
    double? ProjectedUtilizationPct,
    bool ProjectedToExceedBudget);

/// <summary>
/// Trend vs the immediately-preceding window of the same length. Carries the
/// prior window's totals and the percentage delta of the requested window vs the
/// prior window (delta is <c>null</c> when the prior window is empty — no
/// divide-by-zero).
/// </summary>
public sealed record CostTrend(
    decimal PlatformBilledUsd,
    double? PlatformBilledDeltaPct,
    decimal ByokCostUsd,
    double? ByokCostDeltaPct);

/// <summary>Response envelope for <c>GET …/analytics/cost</c>.</summary>
public sealed record CostAnalyticsResponse(
    Guid TenantId,
    CostWindow Window,
    string? GroupBy,
    IReadOnlyList<CostSeriesBucket> Series,
    CostSummary Summary,
    CostTrend Trend);
