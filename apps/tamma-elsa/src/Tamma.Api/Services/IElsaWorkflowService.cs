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
    /// <c>adl-merge-approval-{issue}-{pr}</c> bookmark, injecting the
    /// <c>{decision, feedback, approver}</c> payload as workflow input. Forwards
    /// to the engine's in-process resume endpoint (which owns
    /// <c>IBookmarkStore</c>/<c>IWorkflowRuntime</c>). Returns the resolved
    /// workflow instance id on success.
    /// </summary>
    Task<MergeApprovalResumeResult> ResumeMergeApprovalAsync(
        int issueNumber, int prNumber, string decision, string? feedback, string? approver);
}

/// <summary>Outcome of a merge-approval gate resume.</summary>
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
