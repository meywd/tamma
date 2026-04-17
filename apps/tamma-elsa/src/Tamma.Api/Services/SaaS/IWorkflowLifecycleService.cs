using System.Text.Json;

namespace Tamma.Api.Services.SaaS;

/// <summary>Outcome of a workflow lifecycle operation.</summary>
/// <param name="Success">True when the instance was updated.</param>
/// <param name="ErrorReason">
/// Short machine-readable reason when <see cref="Success"/> is false
/// (e.g. <c>not_found</c>).
/// </param>
public sealed record WorkflowLifecycleResult(bool Success, string? ErrorReason);

/// <summary>
/// Handles progress + terminal-result reporting from external workers
/// (GitHub Actions, self-hosted runners) for a workflow instance.
/// </summary>
public interface IWorkflowLifecycleService
{
    /// <summary>
    /// Update a workflow instance's status and merge worker-provided variables
    /// into the persisted JSONB <c>Variables</c> column.
    /// </summary>
    /// <param name="instanceId">Workflow instance id.</param>
    /// <param name="status">New status (e.g. <c>running</c>, <c>queued</c>).</param>
    /// <param name="variables">Optional JSON object merged over existing variables. Non-object values are ignored.</param>
    Task<WorkflowLifecycleResult> UpdateStatusAsync(
        Guid instanceId,
        string status,
        JsonElement? variables);

    /// <summary>
    /// Record a terminal result for a workflow instance. Marks it
    /// <c>completed</c> or <c>failed</c>, persists the full result payload to
    /// the new <c>Result</c> JSONB column, and emits a
    /// <c>WORKFLOW.COMPLETED</c> / <c>WORKFLOW.FAILED</c> audit event.
    /// </summary>
    /// <param name="instanceId">Workflow instance id.</param>
    /// <param name="result">Result payload (arbitrary JSON).</param>
    /// <param name="success">True if the workflow succeeded.</param>
    Task<WorkflowLifecycleResult> RecordResultAsync(
        Guid instanceId,
        JsonElement result,
        bool success);
}
