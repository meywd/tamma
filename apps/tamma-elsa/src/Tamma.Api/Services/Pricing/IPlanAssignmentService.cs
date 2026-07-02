using Tamma.Data.Entities;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-4 — the REAL, audited plan-assignment surface. A
/// <c>TenantPlanAssignment</c> row is the source of truth for "what plan version
/// is this tenant on right now"; this service is the ONLY writer of the
/// lockstep <c>Tenant.PlanId</c> / <c>Tenant.Plan</c> cache columns.
///
/// <para>Every write pins the plan <c>Version</c> (never "latest"), keeps
/// exactly one <c>active</c> row per tenant (partial unique index +
/// transactional swap), classifies the change direction, flags (never blocks)
/// over-limit downgrades, and emits a <c>TENANT.PLAN.CHANGED</c> /
/// <c>TENANT.PLAN.CANCELLED</c> DCB event carrying the proration boundary Epic
/// 35 Billing consumes. The <c>TENANT.PLAN.CHANGED</c> emit is what evicts Story
/// 34-6's cached entitlement snapshot for the tenant.</para>
/// </summary>
public interface IPlanAssignmentService
{
    /// <summary>
    /// Pin the tenant to a plan version. Guards: rejects a <c>draft</c> plan
    /// always and a <c>deprecated</c> plan unless <see cref="AssignPlanOptions.Force"/>;
    /// rejects a custom (<c>IsCustom</c>) plan not bound to this tenant. Flips the
    /// prior active row to <c>cancelled</c> and inserts a new <c>active</c> row in
    /// one transaction, then updates the lockstep tenant columns. Idempotent — a
    /// re-assign of the same <c>(PlanId, PlanVersion)</c> is a <c>lateral</c> no-op
    /// (returns the existing row, emits no event).
    /// </summary>
    /// <exception cref="Tamma.Core.TammaError">
    /// <c>PLAN.ASSIGN.PLAN_NOT_FOUND</c> / <c>PLAN.ASSIGN.PLAN_DRAFT</c> /
    /// <c>PLAN.ASSIGN.PLAN_DEPRECATED</c> / <c>PLAN.ASSIGN.CUSTOM_PLAN_MISBOUND</c>
    /// / <c>PLAN.ASSIGN.TENANT_NOT_FOUND</c> / <c>PLAN.ASSIGN.CONCURRENT</c>.
    /// </exception>
    Task<PlanAssignmentResult> AssignAsync(
        Guid tenantId, Guid planId, AssignPlanOptions opts, CancellationToken ct = default);

    /// <summary>
    /// Schedule a cancel → <c>plan_free</c>. Does NOT drop the tenant immediately:
    /// stamps <c>EffectiveTo</c> on the current active row at the period boundary
    /// (or now, if <see cref="CancelPlanOptions.Immediate"/>), inserts a
    /// <c>scheduled</c> <c>plan_free</c> row at that boundary, enqueues the
    /// boundary-activation task, and emits <c>TENANT.PLAN.CANCELLED</c>. Already
    /// on <c>plan_free</c> ⇒ no-op.
    /// </summary>
    Task<PlanAssignmentResult> CancelAsync(
        Guid tenantId, CancelPlanOptions opts, CancellationToken ct = default);

    /// <summary>
    /// Promote a due <c>scheduled</c> assignment to <c>active</c> (called by the
    /// platform-queue boundary task). Flips the expiring <c>active</c> row to
    /// <c>cancelled</c>, the scheduled row to <c>active</c>, updates the tenant
    /// columns, and emits <c>TENANT.PLAN.CHANGED</c> with
    /// <c>source=scheduled-activation</c>. Idempotent by <paramref name="assignmentId"/>
    /// — a re-run whose target is already active is a no-op.
    /// </summary>
    Task<PlanAssignmentResult?> ActivateScheduledAsync(
        Guid tenantId, Guid assignmentId, CancellationToken ct = default);

    /// <summary>
    /// The tenant's current effective <c>(PlanId, PlanVersion)</c> assignment, or
    /// <c>null</c> when the tenant has no active assignment. Read-only.
    /// </summary>
    Task<TenantPlanAssignment?> GetActiveAsync(Guid tenantId, CancellationToken ct = default);
}
