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
/// Tunables for the monitor polling loop.
/// </summary>
public sealed record AgentMonitorOptions(
    int PollIntervalSeconds,
    int TimeoutMinutes,
    int DiscoveryTimeoutSeconds = 120,
    int MaxConsecutiveErrors = 5)
{
    public static AgentMonitorOptions Default { get; } =
        new(PollIntervalSeconds: 30, TimeoutMinutes: 35);
}
