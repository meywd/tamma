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
    /// <param name="currentActivity">
    /// Optional new value for <see cref="Tamma.Data.Entities.WorkflowInstance.CurrentActivity"/>.
    /// Audit finding 018 — the SaaS <c>step</c> field maps here so the
    /// dashboard "current step" tile reflects worker progress.
    /// </param>
    Task<WorkflowLifecycleResult> UpdateStatusAsync(
        Guid instanceId,
        string status,
        JsonElement? variables,
        string? currentActivity = null);

    /// <summary>
    /// Record a terminal result for a workflow instance. Marks it
    /// <c>completed</c>, <c>failed</c>, or <c>cancelled</c>, persists the full
    /// result payload to the new <c>Result</c> JSONB column, and emits a
    /// <c>WORKFLOW.COMPLETED</c> / <c>WORKFLOW.FAILED</c> / <c>WORKFLOW.CANCELLED</c>
    /// audit event.
    ///
    /// <para>Audit finding 019: the bool-only signature collapsed
    /// <c>completed | failed | cancelled</c> into a single boolean and the
    /// dashboard could no longer distinguish cancellations from failures
    /// (which inflated the failure-rate SLA metric). The string overload
    /// preserves the three-way state.</para>
    /// </summary>
    Task<WorkflowLifecycleResult> RecordResultAsync(
        Guid instanceId,
        JsonElement result,
        string terminalStatus);
}
