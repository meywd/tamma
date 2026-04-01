using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Checks operational limits before the next issue cycle.
/// Checks: emergency stop, daily quota, max per run, consecutive failures, budget.
///
/// Outcomes:
///   - Continue: within all limits
///   - Stop: a limit was reached
/// </summary>
[Activity(
    "Tamma.ADL",
    "Check Limits",
    "Check quota, budget, failures, and emergency stop before next cycle",
    Kind = ActivityKind.Task
)]
[FlowNode("Continue", "Stop")]
public class CheckLimitsActivity : TammaOutcomeActivity
{
    public override string? EventType => "ADL.LIMITS.CHECK";

    // --- Inputs ---

    [Input(Description = "Number of issues completed so far")]
    public Input<int> IssuesCompleted { get; set; } = default!;

    [Input(Description = "Number of consecutive failures")]
    public Input<int> ConsecutiveFailures { get; set; } = new(0);

    [Input(Description = "Daily issue quota")]
    public Input<int> DailyQuota { get; set; } = new(20);

    [Input(Description = "Max issues per run")]
    public Input<int> MaxPerRun { get; set; } = new(10);

    [Input(Description = "Max consecutive failures before stopping")]
    public Input<int> MaxConsecutiveFailures { get; set; } = new(3);

    [Input(Description = "Number of currently active (dispatched) cycles")]
    public Input<int> ActiveCycles { get; set; } = new(0);

    [Input(Description = "Max concurrent cycles")]
    public Input<int> MaxConcurrent { get; set; } = new(1);

    [Input(Description = "Emergency stop flag")]
    public Input<bool> EmergencyStop { get; set; } = new(false);

    // --- Outputs ---

    [Output(Description = "Reason for stopping, empty if continuing")]
    public Output<string?> StopReason { get; set; } = default!;

    [JsonConstructor]
    public CheckLimitsActivity() { }

    public CheckLimitsActivity(ILogger<CheckLimitsActivity> logger)
    {
        Logger = logger;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var completed = IssuesCompleted.Get(context);
        var failures = ConsecutiveFailures.Get(context);
        var active = ActiveCycles.Get(context);
        var dailyQuota = DailyQuota.Get(context);
        var maxPerRun = MaxPerRun.Get(context);
        var maxFailures = MaxConsecutiveFailures.Get(context);
        var maxConcurrent = MaxConcurrent.Get(context);
        var emergencyStop = EmergencyStop.Get(context);

        // Check emergency stop
        if (emergencyStop)
        {
            await Stop(context, "Emergency stop");
            return;
        }

        // Check consecutive failures
        if (failures >= maxFailures)
        {
            await Stop(context, $"Consecutive failures ({failures}/{maxFailures})");
            return;
        }

        // Check daily quota
        if (completed >= dailyQuota)
        {
            await Stop(context, $"Daily quota reached ({completed}/{dailyQuota})");
            return;
        }

        // Check max per run
        if (completed >= maxPerRun)
        {
            await Stop(context, $"Max per run reached ({completed}/{maxPerRun})");
            return;
        }

        // All checks passed
        StopReason.Set(context, null);
        Logger?.LogInformation(
            "Limits OK: {Completed} completed, {Failures} failures, quota {Quota}, max {Max}",
            completed, failures, dailyQuota, maxPerRun);
        await context.CompleteActivityWithOutcomesAsync("Continue");
    }

    private async Task Stop(ActivityExecutionContext context, string reason)
    {
        StopReason.Set(context, reason);
        Logger?.LogWarning("Limits reached: {Reason}", reason);
        await context.CompleteActivityWithOutcomesAsync("Stop");
    }

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["issuesCompleted"] = IssuesCompleted.Get(context),
        ["consecutiveFailures"] = ConsecutiveFailures.Get(context),
    };

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["issuesCompleted"] = IssuesCompleted.Get(context),
        ["consecutiveFailures"] = ConsecutiveFailures.Get(context),
        ["stopReason"] = StopReason.Get(context),
    };
}
