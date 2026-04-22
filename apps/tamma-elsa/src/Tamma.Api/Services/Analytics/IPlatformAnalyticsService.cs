namespace Tamma.Api.Services.Analytics;

/// <summary>
/// Read-side aggregation port for the platform-admin analytics rollup
/// (Story 28-10). Implementations compute cross-tenant counters on demand
/// from the existing CP + legacy tables until the full
/// <c>platform_analytics_hourly</c> fact table + hourly rollup workflow
/// ship. Swapping to the hourly rollup is behind this interface so the
/// admin endpoint contract does not change.
///
/// <para>Every method MUST be safe to call from a platform-admin context
/// only — the underlying queries read across every tenant regardless of
/// the caller's <c>TenantId</c>. Gate calls behind the <c>OwnerAccess</c>
/// policy at the endpoint layer.</para>
/// </summary>
public interface IPlatformAnalyticsService
{
    /// <summary>
    /// Single-call summary: tenant counts, workflow counters (24h / 7d /
    /// 30d), agent-dispatch counters, and cost aggregates. Powers the
    /// admin dashboard's default view.
    /// </summary>
    Task<PlatformAnalyticsSummary> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the top-N tenants ordered by 30-day workflow volume. Empty
    /// tenants (zero workflow instances in the window) are omitted.
    /// </summary>
    /// <param name="limit">1..200. Values outside the range are clamped.</param>
    Task<IReadOnlyList<TenantAnalyticsRow>> GetTopTenantsAsync(
        int limit = 25,
        CancellationToken ct = default);

    /// <summary>
    /// Event-type histogram for the window <c>[since, now)</c>. Reads from
    /// <c>platform_events</c>. Ordered by count descending.
    /// </summary>
    /// <param name="since">Lower bound (inclusive). Defaults to 24h ago.</param>
    /// <param name="limit">Max buckets to return (1..100). Clamped.</param>
    Task<IReadOnlyList<EventTypeBucket>> GetEventHistogramAsync(
        DateTime? since = null,
        int limit = 20,
        CancellationToken ct = default);
}
