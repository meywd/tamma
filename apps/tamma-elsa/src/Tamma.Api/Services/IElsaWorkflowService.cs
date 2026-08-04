namespace Tamma.Api.Services;

/// <summary>
/// Service for interacting with ELSA workflows
/// </summary>
public interface IElsaWorkflowService
{
    /// <summary>Start a workflow by name</summary>
    Task<string> StartWorkflowAsync(string workflowName, Dictionary<string, object> input);

    /// <summary>Pause a running workflow</summary>
    Task PauseWorkflowAsync(string instanceId);

    /// <summary>Resume a paused workflow</summary>
    Task ResumeWorkflowAsync(string instanceId);

    /// <summary>Cancel a running workflow</summary>
    Task CancelWorkflowAsync(string instanceId);

    /// <summary>Get workflow status</summary>
    Task<WorkflowStatus> GetWorkflowStatusAsync(string instanceId);

    /// <summary>Send a signal to a workflow</summary>
    Task SendSignalAsync(string instanceId, string signalName, object? payload = null);

    /// <summary>
    /// IMPORTANT-2 — resume the <c>merge-approval</c> human gate suspended on the
    /// tenant+repo-scoped bookmark
    /// <c>adl-merge-approval-{tenant}-{repo}-{issue}-{pr}</c>, injecting the
    /// <c>{decision, feedback, approver}</c> payload as workflow input. Forwards
    /// to the engine's in-process resume endpoint (which owns
    /// <c>IBookmarkStore</c>/<c>IWorkflowRuntime</c>). Returns the resolved
    /// workflow instance id on success.
    ///
    /// <para>SECURITY C1/C2 — <paramref name="tenantId"/> and
    /// <paramref name="repository"/> scope the bookmark lookup. The caller
    /// (<c>AdlEndpoints</c>) supplies the AMBIENT tenant id, so the engine can
    /// only ever resolve a gate in that tenant; a cross-tenant attempt resolves
    /// no bookmark (GateNotFound).</para>
    /// </summary>
    Task<MergeApprovalResumeResult> ResumeMergeApprovalAsync(
        int issueNumber, int prNumber, string? tenantId, string? repository,
        string decision, string? feedback, string? approver);

    /// <summary>
    /// Story 43-14 (D3) — LOCATE the suspended merge-approval gate (find its
    /// bookmark + run correlation) WITHOUT running it, so the caller can mint the
    /// merge-composite correlation-standing grant against the run correlation
    /// BEFORE resuming (the engine resume runs the merge synchronously, so a mint
    /// after would 409 the merge). Returns <see cref="ApprovalGateLocation.Found"/>
    /// = false when no gate is suspended.
    /// </summary>
    Task<ApprovalGateLocation> LocateMergeApprovalGateAsync(
        int issueNumber, int prNumber, string? tenantId, string? repository);

    /// <summary>
    /// Story 43-14 (D3) — LOCATE the suspended production-deploy approval gate
    /// (bookmark + run correlation) WITHOUT running, so the deploy-tail grants are
    /// minted before resume.
    /// </summary>
    Task<ApprovalGateLocation> LocateDeploymentApprovalGateAsync(
        int issueNumber, string? tenantId, string? repository, string? mergeSha);

    /// <summary>
    /// Completeness audit P0 item 3 — resume the <c>deployment-pipeline</c>
    /// production-approval human gate suspended on the tenant+repo+SHA-scoped
    /// bookmark <c>adl-deploy-prod-approval-{tenant}-{repo}-{issue}-{mergeSha}</c>,
    /// injecting the <c>{decision, feedback, approver}</c> payload as workflow
    /// input. Forwards to the engine's in-process resume endpoint. Returns the
    /// resolved workflow instance id on success.
    ///
    /// <para>Tenant scoping — <paramref name="tenantId"/> /
    /// <paramref name="repository"/> / <paramref name="mergeSha"/> scope the
    /// bookmark lookup, so the engine can only resolve a gate in the caller's own
    /// tenant; a cross-tenant attempt resolves no bookmark (GateNotFound).</para>
    /// </summary>
    Task<MergeApprovalResumeResult> ResumeDeploymentApprovalAsync(
        int issueNumber, string? tenantId, string? repository, string? mergeSha,
        string decision, string? feedback, string? approver);

    /// <summary>
    /// Follow-up #15 — resume the <c>blocker-diagnosis</c> progressive resolution ladder
    /// suspended on a session-scoped bookmark, injecting the payload the suspend-side
    /// activity callback reads:
    /// <list type="bullet">
    ///   <item><description><c>kind == "progress"</c> → the per-level progress bookmark
    ///     <c>blocker-progress-{session}-{level}</c> with
    ///     <c>{ProgressDetected=true, ProgressType, Details}</c>; and</description></item>
    ///   <item><description><c>kind == "escalation"</c> → the escalation bookmark
    ///     <c>blocker-escalation-{session}</c> with
    ///     <c>{Resolved, SeniorResponse}</c>.</description></item>
    /// </list>
    /// Forwards to the engine's in-process resume endpoint (which owns
    /// <c>IBookmarkStore</c>/<c>IWorkflowRuntime</c>). A 404 (no wait suspended) is surfaced
    /// as <see cref="MergeApprovalResumeResult.GateNotFound"/> rather than thrown.
    ///
    /// <para>SECURITY — the blocker bookmark is keyed by the (unguessable) session id only,
    /// so the cross-tenant guard is enforced by the caller (<c>AdlEndpoints</c>): it verifies
    /// the caller's ambient tenant OWNS <paramref name="sessionId"/> (tenant-scoped session
    /// lookup) BEFORE invoking this. <paramref name="resolver"/> is the server-derived acting
    /// identity (I2), logged by the engine for the audit trail.</para>
    /// </summary>
    Task<MergeApprovalResumeResult> ResumeBlockerResolutionAsync(
        Guid sessionId, string kind, string? level, bool resolved,
        string? progressType, string? details, string? seniorResponse, string? resolver);

    /// <summary>
    /// Story 3.5 — resume the clarifying-questions workflow's answer gate. Forwards to the
    /// engine's in-process resume endpoint (which owns <c>IBookmarkStore</c>/
    /// <c>IWorkflowRuntime</c>), which looks up the tenant+session-scoped
    /// <c>clarify-answers-{tenant}-{session}</c> bookmark and runs the owning instance with
    /// <c>{Answered, Answers}</c> injected as input. A 404 (no wait suspended) is surfaced as
    /// <see cref="MergeApprovalResumeResult.GateNotFound"/> rather than thrown.
    ///
    /// <para>SECURITY — the bookmark name folds in <paramref name="tenantId"/> (the caller's
    /// ambient tenant, derived server-side by <c>AdlEndpoints</c>), so a caller can only ever
    /// resolve a gate in its OWN tenant (cross-tenant → 404). <paramref name="resolver"/> is
    /// the server-derived acting identity (I2), logged by the engine for the audit trail.</para>
    /// </summary>
    Task<MergeApprovalResumeResult> ResumeClarifyingQuestionsAsync(
        Guid sessionId, string? tenantId, string answers, string? resolver);

    /// <summary>
    /// Story 3.7 — resume the design-proposal workflow's human review gate. Forwards to the
    /// engine's in-process resume endpoint (which owns <c>IBookmarkStore</c>/
    /// <c>IWorkflowRuntime</c>), which looks up the tenant+session-scoped
    /// <c>design-approval-{tenant}-{session}</c> bookmark and runs the owning instance with
    /// <c>{Approved, Feedback}</c> injected as input (the gate branches Approved/Rejected off
    /// the flag). A 404 (no wait suspended) is surfaced as
    /// <see cref="MergeApprovalResumeResult.GateNotFound"/> rather than thrown.
    ///
    /// <para>SECURITY — the bookmark name folds in <paramref name="tenantId"/> (the caller's
    /// ambient tenant, derived server-side by <c>AdlEndpoints</c>), so a caller can only ever
    /// resolve a gate in its OWN tenant (cross-tenant → 404). <paramref name="reviewer"/> is
    /// the server-derived acting identity (I2), logged by the engine for the audit trail.</para>
    /// </summary>
    Task<MergeApprovalResumeResult> ResumeDesignApprovalAsync(
        Guid sessionId, string? tenantId, bool approved, string? feedback, string? reviewer);

    /// <summary>
    /// Story 39-8 — resume the ONE generic document-decision gate. Forwards to the engine's
    /// in-process resume endpoint (which owns <c>IBookmarkStore</c>/<c>IWorkflowRuntime</c>),
    /// which looks up the tenant+session-scoped <c>document-decision-{tenant}-{session}</c>
    /// bookmark and runs the owning instance with the
    /// <c>{DecisionJson, Feedback, DeciderId, DeciderDisplay, Channel, RulesReference}</c>
    /// payload injected (the gate maps <c>DecisionJson</c> onto the 39-5 <c>AcceptanceDecision</c>
    /// and branches Accept/RequestRevision/Reject/Escalate off it). A 404 (no wait suspended) is
    /// surfaced as <see cref="MergeApprovalResumeResult.GateNotFound"/> rather than thrown.
    ///
    /// <para>SECURITY — the bookmark name folds in <paramref name="tenantId"/> (the caller's
    /// ambient tenant, derived server-side), so a caller can only ever resolve a gate in its OWN
    /// tenant (cross-tenant → 404). <paramref name="deciderId"/>/<paramref name="channel"/> are
    /// server-derived (D6/D7), never trusted from the client body.</para>
    /// </summary>
    Task<MergeApprovalResumeResult> ResumeDocumentDecisionAsync(
        Guid sessionId, string? tenantId, string decisionJson, string? feedback,
        string? deciderId, string? deciderDisplay, string channel, string? rulesReference);
}

/// <summary>Outcome of a merge-approval (or deploy-approval) gate resume.</summary>
public sealed record MergeApprovalResumeResult(
    bool Resumed,
    bool GateNotFound,
    string? WorkflowInstanceId);

/// <summary>
/// Story 43-14 (D3) — the located gate: its instance id and the RUN correlation
/// its mediated calls carry, so the caller mints against
/// <c>CorrelationId ?? WorkflowInstanceId</c>. <see cref="Found"/> false = no gate
/// suspended (the resume would 404), so nothing is minted.
/// </summary>
public sealed record ApprovalGateLocation(
    bool Found,
    string? WorkflowInstanceId,
    string? CorrelationId);

/// <summary>
/// Workflow status information
/// </summary>
public class WorkflowStatus
{
    public string InstanceId { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? CurrentActivity { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Dictionary<string, object> Variables { get; set; } = new();
}
