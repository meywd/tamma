namespace Tamma.Api.Services.Analytics;

/// <summary>
/// Story 36-3 — the single read seam over the per-tenant Story 36-1 fact
/// tables (<c>analytics_usage_hourly</c> / <c>analytics_usage_daily</c>). Reads
/// pre-aggregated facts ONLY through <c>ITenantDbContextFactory.CreateAsync</c>
/// (per-tenant schema isolation) — it never re-scans <c>domain_events</c> and
/// never touches the control-plane <c>IPlatformAnalyticsService</c>.
///
/// <para>Isolation is physical: the factory binds a schema-scoped connection,
/// so a call for tenant A can only ever see tenant A's schema. There is no
/// <c>TenantId</c> column on the fact tables and no tenant predicate in the
/// query (Story 36-1 §1.4).</para>
/// </summary>
public interface ITenantAnalyticsService
{
    /// <summary>
    /// Time-bucketed usage over the (already-clamped, UTC) <paramref name="window"/>.
    /// With <paramref name="groupBy"/> <c>null</c>, one summed row per bucket;
    /// with it set, one row per <c>(bucket, dimension-value)</c> — the <c>NULL</c>
    /// dimension bucket surfaced with a <c>null</c> key.
    /// </summary>
    Task<UsageResponse> GetUsageAsync(
        Guid tenantId,
        AnalyticsWindow window,
        AnalyticsDimension? groupBy,
        CancellationToken ct);

    /// <summary>
    /// Top-<paramref name="limit"/> rows for a single <paramref name="dimension"/>
    /// over the window, ranked by <paramref name="metric"/> descending.
    /// </summary>
    Task<BreakdownResponse> GetBreakdownAsync(
        Guid tenantId,
        AnalyticsWindow window,
        AnalyticsDimension dimension,
        AnalyticsMetric metric,
        int limit,
        CancellationToken ct);
}
