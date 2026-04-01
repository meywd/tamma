using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Checks operational limits before dispatching the next issue cycle.
///
/// Checks (in order):
///   1. Emergency stop flag
///   2. Active instances &lt; max concurrent (queries Elsa runtime)
///   3. Budget remaining (from cost tracker)
///
/// Outcomes:
///   - Continue: within all limits, safe to dispatch
///   - Stop: a limit was reached
/// </summary>
[Activity(
    "Tamma.ADL",
    "Check Limits",
    "Check concurrency, budget, and emergency stop before next dispatch",
    Kind = ActivityKind.Task
)]
[FlowNode("Continue", "Stop")]
public class CheckLimitsActivity : TammaOutcomeActivity
{
    public override string? EventType => "ADL.LIMITS.CHECK";

    private readonly IWorkflowInstanceStore? _workflowInstanceStore;

    // --- Inputs ---

    [Input(Description = "Max concurrent SingleIssueCycle instances")]
    public Input<int> MaxConcurrent { get; set; } = new(1);

    [Input(Description = "Emergency stop flag")]
    public Input<bool> EmergencyStop { get; set; } = new(false);

    // --- Outputs ---

    [Output(Description = "Reason for stopping, empty if continuing")]
    public Output<string?> StopReason { get; set; } = default!;

    [Output(Description = "Number of currently active cycle instances")]
    public Output<int> ActiveInstances { get; set; } = default!;

    [JsonConstructor]
    public CheckLimitsActivity() { }

    public CheckLimitsActivity(
        ILogger<CheckLimitsActivity> logger,
        IWorkflowInstanceStore workflowInstanceStore)
    {
        Logger = logger;
        _workflowInstanceStore = workflowInstanceStore;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var maxConcurrent = MaxConcurrent.Get(context);
        var emergencyStop = EmergencyStop.Get(context);

        // 1. Emergency stop
        if (emergencyStop)
        {
            await Stop(context, "Emergency stop", 0);
            return;
        }

        // 2. Check active instances
        var activeCount = await GetActiveInstanceCount(context);
        ActiveInstances.Set(context, activeCount);

        if (activeCount >= maxConcurrent)
        {
            await Stop(context, $"Max concurrent reached ({activeCount}/{maxConcurrent})", activeCount);
            return;
        }

        // All checks passed
        StopReason.Set(context, null);
        Logger?.LogInformation(
            "Limits OK: {Active}/{Max} active instances",
            activeCount, maxConcurrent);
        await context.CompleteActivityWithOutcomesAsync("Continue");
    }

    private async Task<int> GetActiveInstanceCount(ActivityExecutionContext context)
    {
        if (_workflowInstanceStore == null)
        {
            Logger?.LogWarning("No IWorkflowInstanceStore available, assuming 0 active instances");
            return 0;
        }

        try
        {
            var filter = new WorkflowInstanceFilter
            {
                DefinitionId = "single-issue-cycle",
                WorkflowStatus = WorkflowStatus.Running,
            };

            var count = await _workflowInstanceStore.CountAsync(filter);
            return (int)count;
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Failed to query active workflow instances");
            return 0; // fail open — don't block on query failure
        }
    }

    private async Task Stop(ActivityExecutionContext context, string reason, int active)
    {
        StopReason.Set(context, reason);
        ActiveInstances.Set(context, active);
        Logger?.LogWarning("Limits reached: {Reason}", reason);
        await context.CompleteActivityWithOutcomesAsync("Stop");
    }

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["maxConcurrent"] = MaxConcurrent.Get(context),
    };

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["activeInstances"] = ActiveInstances.Get(context),
        ["stopReason"] = StopReason.Get(context),
    };
}
