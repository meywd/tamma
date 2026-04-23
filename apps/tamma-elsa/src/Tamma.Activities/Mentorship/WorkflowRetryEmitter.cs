using Tamma.Data.Abstractions;

namespace Tamma.Activities.Mentorship;

/// <summary>
/// Wave C.4 §3 — shared helper that emits <c>WORKFLOW.RETRY_EXCEEDED</c>
/// when a retry-budgeted activity hits its ceiling. Used by the three
/// Mentorship FlowNode activities whose MaxAttempts input bounds the
/// retry loop (<c>ClarifyRequirementsActivity</c>,
/// <c>ReExplainStoryActivity</c>, <c>AutoFixIssuesActivity</c>).
///
/// <para>The helper is standalone (rather than an extension method on
/// <c>ActivityExecutionContext</c>) so tests can exercise it without
/// hosting Elsa. Every activity call site resolves
/// <see cref="IAlertEventEmitter"/> from the context's DI and forwards
/// the relevant correlation ids.</para>
/// </summary>
public static class WorkflowRetryEmitter
{
    /// <summary>
    /// Emit WORKFLOW.RETRY_EXCEEDED. No-op if
    /// <paramref name="emitter"/> or <paramref name="tenantId"/> is null
    /// — the rule-engine groups by tenantId; a null-tenant emission
    /// defeats grouping and isn't useful.
    /// </summary>
    public static async Task EmitAsync(
        IAlertEventEmitter? emitter,
        Guid? tenantId,
        Guid workflowDefinitionId,
        Guid workflowInstanceId,
        int attempts,
        int maxAttempts,
        string? finalError,
        string? activityId,
        CancellationToken ct)
    {
        if (emitter is null) return;
        if (tenantId is not Guid tid) return;

        await emitter.EmitWorkflowRetryExceededAsync(
            new WorkflowRetryExceededEvent(
                TenantId: tid,
                CorrelationId: workflowInstanceId.ToString("N"),
                WorkflowDefinitionId: workflowDefinitionId,
                WorkflowInstanceId: workflowInstanceId,
                Attempts: attempts,
                MaxAttempts: maxAttempts,
                FinalError: finalError,
                ActivityId: activityId), ct).ConfigureAwait(false);
    }
}
