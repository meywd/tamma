namespace Tamma.Api.Services.Analytics;

/// <summary>
/// Story 36-4 — DCB event constant(s) emitted by <see cref="CostAnalyticsService"/>.
/// Follows the <c>AGGREGATE.ACTION.STATUS</c> convention (CLAUDE.md "Event Types").
/// </summary>
public static class CostAnalyticsEvents
{
    /// <summary>
    /// Emitted (best-effort, per-<c>(tenantId, period)</c> deduped) when a
    /// tenant's linear run-rate projection for the current calendar period
    /// crosses its configured <c>BudgetConfig.LimitUsd</c>. Consumed by the
    /// alerting / scheduled-report pipeline (a downstream Epic 36 / Story 5.6
    /// consumer). Tenant-scoped (<c>DomainEvent.TenantId</c> set).
    /// </summary>
    public const string BudgetProjectedExceeded = "ANALYTICS.COST.BUDGET_PROJECTED_EXCEEDED";
}
