using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Activities.AgentDispatch;

// ================================================================
// Services — the execution logic behind stories 19-2, 19-3, 19-4
// extracted from the activities so GitHubActionsExecutor can reuse
// them without programmatically invoking Elsa activities.
//
// Story 19-5 "Option B" (reuse-via-services, not reuse-via-activities).
// ================================================================

/// <summary>
/// Story 19-2 — dispatches a workflow_dispatch event. Returns the
/// dispatch outcome along with the timestamp the call was made (used
/// by the monitor service to filter out pre-existing runs).
/// </summary>
public interface IAgentDispatchService
{
    Task<AgentDispatchResult> DispatchAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Story 19-3 — monitors a dispatched workflow run until it reaches
/// a terminal state.
/// </summary>
public interface IAgentMonitorService
{
    Task<AgentMonitorResult> MonitorAsync(
        AgentExecutionRequest request,
        DateTime dispatchedAfter,
        AgentMonitorOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Story 19-4 — collects final results (artifact, PR, check runs, files
/// changed) once the workflow has concluded.
/// </summary>
public interface IAgentResultCollectorService
{
    Task<AgentExecutionResult> CollectAsync(
        AgentExecutionRequest request,
        AgentMonitorResult monitorResult,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// How the monitor observes completion of a dispatched workflow run.
///
/// <list type="bullet">
///   <item>
///     <b><see cref="Poll"/></b> — periodically call the GitHub REST API.
///     Back-compat default. Always functional.
///   </item>
///   <item>
///     <b><see cref="Webhook"/></b> — suspend until a
///     <c>workflow_run.completed</c> webhook arrives at
///     <c>POST /api/github/webhooks</c>. No polling at all. If the webhook
///     never arrives the monitor raises a <c>monitor_failed</c> result;
///     use <see cref="Auto"/> if you want a safety-window fallback.
///   </item>
///   <item>
///     <b><see cref="Auto"/></b> — prefer webhook when a
///     <c>WebhookSignalRegistry</c> is wired AND configuration allows it;
///     otherwise poll. If the webhook hasn't fired inside
///     <c>TimeoutMinutes * WebhookSafetyWindowMultiplier</c>, the service
///     falls back to poll mode and catches up. This is the recommended
///     deployment mode for SaaS (reduces GitHub API rate pressure by
///     ~60 calls per agent run while keeping a hard safety net).
///   </item>
/// </list>
/// </summary>
public enum AgentMonitorMode
{
    /// <summary>Webhook with a safety-window fallback to polling.</summary>
    Auto = 0,
    /// <summary>Poll the GitHub REST API. Back-compat default.</summary>
    Poll = 1,
    /// <summary>Suspend until a webhook arrives; no fallback.</summary>
    Webhook = 2
}

/// <summary>
/// Tunables for the monitor polling loop.
/// </summary>
/// <param name="PollIntervalSeconds">Seconds between poll API calls.</param>
/// <param name="TimeoutMinutes">Overall budget for the monitor step.</param>
/// <param name="DiscoveryTimeoutSeconds">Max seconds spent looking for the
/// <c>workflow_run</c> id after dispatch.</param>
/// <param name="MaxConsecutiveErrors">Poll errors in a row before the
/// service bails with <c>monitor_failed</c>.</param>
/// <param name="Mode">
/// How to observe completion. <see cref="AgentMonitorMode.Poll"/> is the
/// back-compat default.
/// </param>
/// <param name="WebhookSafetyWindowMultiplier">
/// Used only when <see cref="Mode"/> is <see cref="AgentMonitorMode.Auto"/>
/// or <see cref="AgentMonitorMode.Webhook"/>. The webhook wait resolves
/// or falls back to poll (Auto only) after
/// <c>TimeoutMinutes * WebhookSafetyWindowMultiplier</c> elapses. Default
/// <c>1.5x</c> — Story 19-3 AC-7 spec.
/// </param>
public sealed record AgentMonitorOptions(
    int PollIntervalSeconds,
    int TimeoutMinutes,
    int DiscoveryTimeoutSeconds = 120,
    int MaxConsecutiveErrors = 5,
    AgentMonitorMode Mode = AgentMonitorMode.Poll,
    double WebhookSafetyWindowMultiplier = 1.5)
{
    public static AgentMonitorOptions Default { get; } =
        new(PollIntervalSeconds: 30, TimeoutMinutes: 35);
}
