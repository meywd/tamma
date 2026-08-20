using Tamma.Activities.ADL.Models;

namespace Tamma.Activities.ADL;

/// <summary>
/// Story 4-6 — central catalogue of the <c>PLAN_APPROVAL.*</c> DCB event types emitted by
/// the human plan-approval gate (<see cref="WaitForPlanApprovalActivity"/>). The gate is the
/// AI-plan checkpoint of the autonomous loop: a generated plan suspends on a bookmark until a
/// human chooses <i>approve / reject / edit</i>. Every transition of that gate is an auditable
/// approval event (the architecture lists "approvals/escalations" as a first-class captured
/// event family).
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same pattern
/// <see cref="EmitMergeApprovalEventActivity"/> uses. No activity holds a DB / repository
/// dependency of its own (none is registered in the Elsa engine — a directly-injected
/// <c>IEventRepository</c> would be inert and silently drop every gate event).</para>
///
/// <para>Type pattern follows the platform's <c>AGGREGATE.ACTION.STATUS</c> convention and
/// mirrors <see cref="MergeApprovalEvents"/> (the merge-approval gate). The request event is
/// <c>PLAN_APPROVAL.REQUESTED</c> (emitted at suspend) and the decision lands as a
/// <c>PLAN_APPROVAL.DECISION.*</c> event on resume.</para>
///
/// <list type="bullet">
///   <item><description><c>PLAN_APPROVAL.REQUESTED</c> — the gate suspended on its bookmark,
///     awaiting a human plan decision.</description></item>
///   <item><description><c>PLAN_APPROVAL.DECISION.APPROVED</c> — a human approved the
///     plan.</description></item>
///   <item><description><c>PLAN_APPROVAL.DECISION.REJECTED</c> — a human rejected the plan
///     (or the resume payload carried an unknown / empty decision — fail-closed, never a
///     silent approve). LOUD (error-status).</description></item>
///   <item><description><c>PLAN_APPROVAL.DECISION.EDIT_REQUESTED</c> — a human requested
///     edits; the cycle loops back to plan generation.</description></item>
/// </list>
/// </summary>
public static class PlanApprovalEvents
{
    public const string Requested = "PLAN_APPROVAL.REQUESTED";
    public const string DecisionApproved = "PLAN_APPROVAL.DECISION.APPROVED";
    public const string DecisionRejected = "PLAN_APPROVAL.DECISION.REJECTED";
    public const string DecisionEditRequested = "PLAN_APPROVAL.DECISION.EDIT_REQUESTED";

    /// <summary>
    /// The durable approval SLA expired with no human decision. LOUD (error-status) —
    /// distinguished from a real rejection so "nobody looked" and "a human said no" are
    /// not the same row in the audit trail.
    /// </summary>
    public const string DecisionTimedOut = "PLAN_APPROVAL.DECISION.TIMED_OUT";

    /// <summary>
    /// Map a resolved <see cref="ApprovalDecision"/> onto its <c>PLAN_APPROVAL.DECISION.*</c>
    /// event type. Approve → APPROVED, Edit → EDIT_REQUESTED; anything else (Reject / Test /
    /// unknown) → REJECTED (fail-closed: an ambiguous decision must never proceed with the
    /// plan). Mirrors the gate's own outcome mapping.
    /// </summary>
    public static string DecisionEventType(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Approve => DecisionApproved,
        ApprovalDecision.Edit => DecisionEditRequested,
        _ => DecisionRejected,
    };

    /// <summary>
    /// Status convention: a rejection is a LOUD (error-status) audit row — the plan was
    /// declined and the cycle ends — mirroring <see cref="MergeApprovalEvents"/> /
    /// <c>DeployEvents</c> where a rejected decision is never recorded as a false success.
    /// Requested / approved / edit-requested are normal (success-status) rows.
    /// </summary>
    public static string StatusForEvent(string type)
        => type is DecisionRejected or DecisionTimedOut ? "error" : "success";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow inputs.
    /// Returns <c>null</c> for empty / single-user / unparseable values (plan-approval events
    /// in single-user mode are platform-scope, TenantId null). Mirrors
    /// <see cref="MergeApprovalEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;
}
