using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL.Models;

namespace Tamma.Activities.ADL;

/// <summary>
/// Checks operational limits: daily quota, budget, emergency stop flag.
/// Used by the ADL orchestrator loop to decide whether to continue.
///
/// Outcomes:
///   - Continue: within limits, proceed with next issue
///   - Stop: limits reached or emergency stop active
/// </summary>
[Activity(
    "Tamma.ADL",
    "Check Limits",
    "Check daily quota, budget, and emergency stop before next cycle",
    Kind = ActivityKind.Task
)]
[FlowNode("Continue", "Stop")]
public class CheckLimitsActivity : Activity
{
    private readonly ILogger<CheckLimitsActivity>? _logger;

    [Input(Description = "Number of issues completed so far in this run")]
    public Input<int> IssuesCompleted { get; set; } = default!;

    [Input(Description = "Configuration JSON with operational limits")]
    public Input<string?> ConfigJson { get; set; } = default!;

    [Output(Description = "Reason for stopping, if applicable")]
    public Output<string?> StopReason { get; set; } = default!;

    [JsonConstructor]
    public CheckLimitsActivity() { }

    public CheckLimitsActivity(ILogger<CheckLimitsActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var issuesCompleted = IssuesCompleted.Get(context);
        var configJson = ConfigJson.Get(context);

        var config = DeserializeConfig(configJson);
        var limits = config.Limits;

        // Check emergency stop
        if (limits.EmergencyStop)
        {
            _logger?.LogWarning("Emergency stop flag is active");
            StopReason.Set(context, "Emergency stop");
            await context.CompleteActivityWithOutcomesAsync("Stop");
            return;
        }

        // Check daily quota
        if (issuesCompleted >= limits.DailyIssueQuota)
        {
            _logger?.LogInformation("Daily issue quota reached: {Completed}/{Quota}",
                issuesCompleted, limits.DailyIssueQuota);
            StopReason.Set(context, $"Daily quota reached ({issuesCompleted}/{limits.DailyIssueQuota})");
            await context.CompleteActivityWithOutcomesAsync("Stop");
            return;
        }

        // Check max issues per run
        if (issuesCompleted >= config.MaxIssuesPerRun)
        {
            _logger?.LogInformation("Max issues per run reached: {Completed}/{Max}",
                issuesCompleted, config.MaxIssuesPerRun);
            StopReason.Set(context, $"Max per run reached ({issuesCompleted}/{config.MaxIssuesPerRun})");
            await context.CompleteActivityWithOutcomesAsync("Stop");
            return;
        }

        _logger?.LogInformation("Limits check passed: {Completed} completed, quota {Quota}",
            issuesCompleted, limits.DailyIssueQuota);
        await context.CompleteActivityWithOutcomesAsync("Continue");
    }

    private static AdlConfig DeserializeConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new AdlConfig();

        try
        {
            return JsonSerializer.Deserialize<AdlConfig>(json) ?? new AdlConfig();
        }
        catch
        {
            return new AdlConfig();
        }
    }
}
