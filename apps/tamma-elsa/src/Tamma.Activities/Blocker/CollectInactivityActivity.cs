using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.Blocker.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Blocker;

/// <summary>
/// Collects inactivity signals: time since last meaningful activity.
/// Designed for parallel execution within the Blocker Diagnosis workflow's Fork/Join.
/// Failed collection does not block — returns a signal with CollectionSucceeded=false.
/// </summary>
[Activity(
    "Tamma.Blocker",
    "Collect Inactivity",
    "Measure time since last meaningful activity for blocker diagnosis",
    Kind = ActivityKind.Task
)]
public class CollectInactivityActivity : CodeActivity<InactivitySignal>
{
    private readonly ILogger<CollectInactivityActivity>? _logger;
    private readonly IIntegrationService? _integrationService;

    /// <summary>Repository URL or owner/repo</summary>
    [Input(Description = "Repository URL or owner/repo")]
    public Input<string> Repository { get; set; } = default!;

    /// <summary>Branch name to check</summary>
    [Input(Description = "Branch name to check")]
    public Input<string> BranchName { get; set; } = default!;

    /// <summary>Threshold in minutes for considering inactivity</summary>
    [Input(Description = "Inactivity threshold in minutes", DefaultValue = 30)]
    public Input<int> InactivityThresholdMinutes { get; set; } = new(30);

    [JsonConstructor]
    public CollectInactivityActivity() { }

    public CollectInactivityActivity(
        ILogger<CollectInactivityActivity> logger,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _integrationService = integrationService;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context);
        var branchName = BranchName.Get(context);
        var thresholdMinutes = InactivityThresholdMinutes.Get(context);

        _logger?.LogInformation(
            "Collecting inactivity signals for {Repository}/{Branch}",
            repository, branchName);

        var signal = new InactivitySignal();

        try
        {
            // Use recent commits as a proxy for activity
            var since = DateTime.UtcNow.AddHours(-24);
            var commits = await _integrationService!.GetGitHubCommitsAsync(repository, branchName, since);

            if (commits.Any())
            {
                var lastCommitTime = commits.Max(c => c.Timestamp);
                signal.LastActivityTime = lastCommitTime;
                signal.LastActivityType = "commit";
                signal.TimeSinceLastActivity = DateTime.UtcNow - lastCommitTime;
            }
            else
            {
                signal.TimeSinceLastActivity = TimeSpan.FromHours(24);
                signal.LastActivityType = "none";
            }

            signal.IsInactive = signal.TimeSinceLastActivity.TotalMinutes > thresholdMinutes;
            signal.CollectionSucceeded = true;

            _logger?.LogInformation(
                "Inactivity collected: TimeSince={TimeSince}min, IsInactive={IsInactive}",
                signal.TimeSinceLastActivity.TotalMinutes, signal.IsInactive);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to collect inactivity signals — continuing with partial data");
            signal.CollectionSucceeded = false;
            signal.Error = ex.Message;
        }

        context.SetResult(signal);
    }
}
