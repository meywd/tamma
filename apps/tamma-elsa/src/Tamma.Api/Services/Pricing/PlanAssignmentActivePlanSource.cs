using Microsoft.Extensions.Logging;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-4 — the REAL <see cref="IActivePlanAssignmentSource"/>: reads the
/// tenant's active <c>TenantPlanAssignment</c> row (via
/// <see cref="IPlanAssignmentService.GetActiveAsync"/>) and returns its pinned
/// <c>(PlanId, PlanVersion)</c>. This is the one-line DI swap Story 34-6
/// anticipated — it replaces the interim
/// <see cref="TenantShadowColumnPlanAssignmentSource"/> (which read the raw
/// Epic-28 <c>PlanId</c> shadow column) behind the same seam, with no change to
/// the entitlement resolver. Because the assignment row carries the pinned
/// version directly, this source supplies <c>PlanVersion</c> (the shadow-column
/// source left it <c>null</c>).
/// </summary>
public sealed class PlanAssignmentActivePlanSource : IActivePlanAssignmentSource
{
    private readonly IPlanAssignmentService _assignments;
    private readonly ILogger<PlanAssignmentActivePlanSource> _logger;

    public PlanAssignmentActivePlanSource(
        IPlanAssignmentService assignments,
        ILogger<PlanAssignmentActivePlanSource> logger)
    {
        _assignments = assignments ?? throw new ArgumentNullException(nameof(assignments));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ActivePlanAssignment?> GetActiveAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        var active = await _assignments.GetActiveAsync(tenantId, ct).ConfigureAwait(false);
        if (active is null)
        {
            _logger.LogDebug(
                "No active plan assignment for tenant {TenantId} (tenant_plan_assignments)",
                tenantId);
            return null;
        }

        return new ActivePlanAssignment(active.PlanId, active.PlanVersion);
    }
}
