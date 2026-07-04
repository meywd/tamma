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
}

/// <summary>Outcome of a merge-approval (or deploy-approval) gate resume.</summary>
public sealed record MergeApprovalResumeResult(
    bool Resumed,
    bool GateNotFound,
    string? WorkflowInstanceId);

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
