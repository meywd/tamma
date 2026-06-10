namespace Tamma.Api.Services.Analytics;

/// <summary>
/// Story 28-10 — DTOs for the platform-wide analytics rollup surfaced under
/// <c>/api/admin/analytics/*</c>. These records are the read-side contract
/// consumed by the admin dashboard (Story 28-11) and any external ops tool
/// that needs a one-query, cross-tenant snapshot of the fleet.
///
/// <para>They are deliberately lightweight projections over the existing CP
/// tables (<c>tenants</c>, <c>platform_events</c>, <c>platform_queued_tasks</c>)
/// plus the legacy <c>workflow_instances</c> / <c>domain_events</c> tables —
/// no new entity, no new migration. When the full Story 28-10
/// <c>platform_analytics_hourly</c> fact table + hourly rollup workflow lands,
/// the service implementation can swap sources over while keeping these
/// shapes stable.</para>
///
/// <para>All time fields are UTC. Window-scoped counters group activity into
/// three horizons — last 24 hours, last 7 days, last 30 days — so a single
/// call answers the dashboard's default tiles without forcing the caller to
/// pick a window up-front.</para>
/// </summary>
public sealed record PlatformAnalyticsSummary(
    TenantCounts Tenants,
    WorkflowCounts Workflows,
    AgentDispatchCounts AgentDispatches,
    CostAggregates Costs,
    DateTime GeneratedAt);

/// <summary>
/// Tenant-directory snapshot. <see cref="Total"/> excludes soft-deleted rows
/// by virtue of the CP context's <c>DeletedAt</c> query filter; the other
/// three columns project the Epic 28 <c>Status</c> shadow column into the
/// operational buckets the dashboard cares about.
/// </summary>
public sealed record TenantCounts(
    int Total,
    int Active,
    int Provisioning,
    int Deleted);

/// <summary>
/// Workflow-instance counters bucketed by window. Each bucket reports
/// completed / failed / running instances whose <c>CreatedAt</c> falls inside
/// the window. "Running" is the union of <c>pending</c> + <c>running</c>
/// storage states so the tile does not flap when Elsa flips between them.
/// </summary>
public sealed record WorkflowCounts(
    WorkflowWindowCounts Last24h,
    WorkflowWindowCounts Last7d,
    WorkflowWindowCounts Last30d);

public sealed record WorkflowWindowCounts(
    int Completed,
    int Failed,
    int Running);

/// <summary>
/// Agent-dispatch counters (Epic 19 <c>AGENT.DISPATCH.*</c> platform events).
/// Success = <c>AGENT.DISPATCH.SUCCESS</c> events in the window. Failed =
/// <c>AGENT.DISPATCH.FAILED</c>. Attempted = every row whose type starts with
/// <c>AGENT.DISPATCH.</c>.
/// </summary>
public sealed record AgentDispatchCounts(
    AgentDispatchWindowCounts Last24h,
    AgentDispatchWindowCounts Last7d,
    AgentDispatchWindowCounts Last30d);

public sealed record AgentDispatchWindowCounts(
    int Attempted,
    int Success,
    int Failed);

/// <summary>
/// Rolled-up LLM / compute cost aggregates in USD. The service reads
/// <c>LLM.CALL.SUCCESS.data.costUsd</c> out of the legacy
/// <c>domain_events</c> stream — the same shape emitted by LLM-call
/// activities — and sums per window. Values are capped at four decimals
/// (same precision as the future <c>platform_analytics_hourly.Value
/// NUMERIC(20,4)</c> column) so the round-trip from the hourly rollup is
/// lossless.
/// </summary>
public sealed record CostAggregates(
    decimal Last24hUsd,
    decimal Last7dUsd,
    decimal Last30dUsd);

/// <summary>
/// Tenant-scoped rollup row for the "top tenants" admin view. Ordered by
/// <see cref="WorkflowsLast30d"/> descending so ops can see which tenants
/// drove the most traffic in the recent window.
/// </summary>
public sealed record TenantAnalyticsRow(
    Guid TenantId,
    string Slug,
    string Name,
    string Plan,
    string? Status,
    int WorkflowsLast30d,
    int WorkflowsFailedLast30d,
    decimal CostUsdLast30d);

/// <summary>
/// Event-type histogram bucket for the <c>GET /api/admin/analytics/events</c>
/// endpoint. Powers the admin dashboard's "recent events" breakdown chart.
/// </summary>
public sealed record EventTypeBucket(
    string Type,
    int Count);

/// <summary>
/// Story 28-11 AC2 — per-tenant 24-hour resource rollup surfaced on the
/// admin tenant-detail response (<c>resourceSummary</c>). Every field is a
/// sum of the tenant's <c>platform_analytics_hourly</c> rows whose
/// <c>Hour</c> falls inside <c>[now-24h, now)</c>. A tenant with no rows yet
/// (freshly provisioned) gets an all-zero instance via
/// <see cref="Empty"/> — never null, never an error.
///
/// <para><b>Metric-model caveat:</b> only the columns that actually exist on
/// <see cref="Tamma.Data.Entities.PlatformAnalyticsHourly"/> are surfaced
/// here. The Story 28-10 spec's <c>apiRequestsLast24h</c> /
/// <c>errorsLast24h</c> (5xx) keys are NOT available because the wide-row
/// fact table does not carry an API-request or HTTP-error counter — that
/// would require the long-narrow MetricKey/Tags model that was deferred.
/// <see cref="WorkflowsFailedLast24h"/> is the nearest real error signal the
/// table tracks (failed workflow instances).</para>
/// </summary>
public sealed record TenantResourceSummary(
    /// <summary>Sum of <c>WorkflowsStarted</c> in the last 24h.</summary>
    long WorkflowsLast24h,
    /// <summary>Sum of <c>WorkflowsCompleted</c> in the last 24h.</summary>
    long WorkflowsCompletedLast24h,
    /// <summary>
    /// Sum of <c>WorkflowsFailed</c> in the last 24h. This is the real
    /// "error" signal the fact table carries (the 28-10 spec's 5xx
    /// <c>errorsLast24h</c> is not available until the long-narrow metric
    /// model lands).
    /// </summary>
    long WorkflowsFailedLast24h,
    /// <summary>Sum of <c>AgentDispatches</c> in the last 24h.</summary>
    long AgentDispatchesLast24h,
    /// <summary>Sum of <c>TokensIn</c> in the last 24h.</summary>
    long TokensInLast24h,
    /// <summary>Sum of <c>TokensOut</c> in the last 24h.</summary>
    long TokensOutLast24h,
    /// <summary>Sum of <c>CostUsd</c> in the last 24h (4-decimal cap).</summary>
    decimal LlmCostUsdLast24h)
{
    /// <summary>
    /// Zeroed summary for a tenant with no <c>platform_analytics_hourly</c>
    /// rows in the window. Returned instead of null so the dashboard cards
    /// render "0" rather than failing on a missing object.
    /// </summary>
    public static TenantResourceSummary Empty { get; } =
        new(0, 0, 0, 0, 0, 0, 0m);
}
