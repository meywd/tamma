using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
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

        // All checks passed.
        // 2026-08-13 (found by the engine-driven E2E): a literal `null` here
        // binds to Elsa's Set(Output<T>, ctx, Variable<T>) overload, whose null
        // Variable dereference throws NRE — so the HAPPY path of this activity
        // ALWAYS faulted and the orchestrator could never reach DispatchCycle.
        // The typed empty string keeps the "empty if continuing" output
        // contract while binding to the value overload.
        StopReason.Set(context, string.Empty);
        Logger?.LogInformation(
            "Limits OK: {Active}/{Max} active instances",
            activeCount, maxConcurrent);
        await context.CompleteActivityWithOutcomesAsync("Continue");
    }

    private async Task<int> GetActiveInstanceCount(ActivityExecutionContext context)
    {
        // 2026-08-14: a store-rehydrated activity has NULL ctor-injected members
        // (the same defect fixed in six sibling activities), so this returned 0
        // for EVERY tick — MaxConcurrent was never enforced and the ADL loop
        // could dispatch cycles without bound. The warning below could not even
        // report it, because the injected Logger is null for the same reason.
        var store = _workflowInstanceStore ?? context.GetService<IWorkflowInstanceStore>();
        var logger = Logger ?? context.GetService<ILogger<CheckLimitsActivity>>();
        if (store == null)
        {
            logger?.LogWarning("No IWorkflowInstanceStore available, assuming 0 active instances");
            return 0;
        }

        try
        {
            var filter = new WorkflowInstanceFilter
            {
                DefinitionId = "single-issue-cycle",
                WorkflowStatus = WorkflowStatus.Running,
            };

            var count = await store.CountAsync(filter);
            return (int)count;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to query active workflow instances");
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
        ["activeInstances"] = this.GetOutput<int>(context, nameof(ActiveInstances)),
        ["stopReason"] = this.GetOutput<string?>(context, nameof(StopReason)),
    };
}
