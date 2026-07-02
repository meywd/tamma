namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Story 34-4 — payload for the platform-queue boundary-activation task that
/// promotes a <c>scheduled</c> <c>TenantPlanAssignment</c> to <c>active</c> once
/// its <c>EffectiveFrom</c> boundary is reached. Enqueued by
/// <c>PlanAssignmentService.CancelAsync</c> (period-end path) with the row's
/// <c>EffectiveFrom</c> as the task's <c>VisibleAt</c>, so the queue itself
/// defers the reservation until the boundary. Mirrors
/// <see cref="MoveTenantTaskPayload"/>: a placement/lifecycle change rides the
/// platform queue.
/// </summary>
public sealed class ActivateScheduledPlanTaskPayload
{
    /// <summary>
    /// Stable task-type identifier the <c>ActivateScheduledPlanTaskHandler</c>
    /// matches on. Dot-separated lower-snake-case, matching the queue convention.
    /// </summary>
    public const string TaskType = "plan.activate_scheduled";

    /// <summary>Tenant whose scheduled assignment is being promoted.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The <c>scheduled</c> assignment row to promote (idempotency key).</summary>
    public Guid AssignmentId { get; set; }
}
