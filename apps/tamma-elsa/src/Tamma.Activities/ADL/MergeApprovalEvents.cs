namespace Tamma.Activities.ADL;

/// <summary>
/// FR-19 / FR-34 / Story 4-6 — central catalogue of the <c>MERGE_APPROVAL.*</c>
/// and <c>MERGE.*</c> DCB event types emitted by the <c>merge-approval</c>
/// workflow gate.
///
/// <para>The gate is the human <b>APPROVAL_GATE</b> step of the 14-step loop
/// (<c>docs/architecture.md</c> line 840): a completed PR suspends on a bookmark
/// until a human chooses <i>merge / test / reject</i>. Every transition of that
/// gate is an auditable approval/escalation event (the architecture lists
/// "approvals/escalations" as a first-class captured event family —
/// <c>docs/stories/epic-4/4-6-event-capture-approvals-escalations.md</c>).</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine
/// event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same
/// pattern <see cref="EmitPrEventActivity"/> uses. No activity holds a DB /
/// repository dependency of its own (none is registered in the Elsa engine — a
/// directly-injected <c>IEventRepository</c> would be inert and silently drop
/// every gate event).</para>
///
/// <para>Type pattern follows the platform's <c>AGGREGATE.ACTION.STATUS</c>
/// convention.</para>
///
/// <list type="bullet">
///   <item><description><c>MERGE_APPROVAL.DECISION.MERGED</c> — a human approved
///     the PR to merge.</description></item>
///   <item><description><c>MERGE_APPROVAL.DECISION.TEST</c> — a human requested
///     more testing before a merge decision.</description></item>
///   <item><description><c>MERGE_APPROVAL.DECISION.REJECTED</c> — a human
///     rejected the PR.</description></item>
///   <item><description><c>MERGE_APPROVAL.DECISION.INVALID</c> — the resume
///     payload carried an unknown / empty decision. Emitted instead of silently
///     defaulting to "reject" (no-silent-failure rule). Routes back to the gate
///     for a valid re-decision.</description></item>
///   <item><description><c>MERGE_APPROVAL.ESCALATED</c> — the gate escalated to
///     owners (e.g. an invalid decision needing human attention, or a
///     breaking-change merge attempted without an authorised approver).</description></item>
///   <item><description><c>MERGE.REQUESTED</c> — on approval the gate dispatched
///     the <c>merge</c> workflow.</description></item>
///   <item><description><c>MERGE_APPROVAL.TEST_REQUESTED</c> — on the test
///     decision the gate dispatched the testing sub-workflow before re-deciding.</description></item>
/// </list>
/// </summary>
public static class MergeApprovalEvents
{
    /// <summary>Activity-level <c>EventType</c> prefix for the bookmark gate
    /// (drives the auto <c>.STARTED</c> / <c>.FAILED</c> events on
    /// <see cref="WaitForMergeApprovalActivity"/>, a <c>TammaOutcomeActivity</c>).</summary>
    public const string GatePrefix = "APPROVAL.GATE";

    public const string DecisionMerged = "MERGE_APPROVAL.DECISION.MERGED";
    public const string DecisionTest = "MERGE_APPROVAL.DECISION.TEST";
    public const string DecisionRejected = "MERGE_APPROVAL.DECISION.REJECTED";
    public const string DecisionInvalid = "MERGE_APPROVAL.DECISION.INVALID";
    public const string Escalated = "MERGE_APPROVAL.ESCALATED";
    public const string MergeRequested = "MERGE.REQUESTED";
    public const string TestRequested = "MERGE_APPROVAL.TEST_REQUESTED";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values
    /// (gate events in single-user mode are platform-scope, TenantId null).
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;
}
