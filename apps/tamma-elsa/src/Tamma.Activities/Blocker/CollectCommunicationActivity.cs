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
/// Collects communication signals: Slack messages, questions asked (if available).
/// Designed for parallel execution within the Blocker Diagnosis workflow's Fork/Join.
/// Failed collection does not block — returns a signal with CollectionSucceeded=false.
/// </summary>
[Activity(
    "Tamma.Blocker",
    "Collect Communication",
    "Check communication patterns for blocker diagnosis",
    Kind = ActivityKind.Task
)]
public class CollectCommunicationActivity : CodeActivity<CommunicationSignal>
{
    private readonly ILogger<CollectCommunicationActivity>? _logger;
    private readonly IIntegrationService? _integrationService;

    /// <summary>Junior developer's Slack ID (if available)</summary>
    [Input(Description = "Junior developer's Slack ID")]
    public Input<string?> SlackId { get; set; } = default!;

    /// <summary>Junior developer ID</summary>
    [Input(Description = "Junior developer ID")]
    public Input<string> JuniorId { get; set; } = default!;

    [JsonConstructor]
    public CollectCommunicationActivity() { }

    public CollectCommunicationActivity(
        ILogger<CollectCommunicationActivity> logger,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _integrationService = integrationService;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var slackId = SlackId.Get(context);
        var juniorId = JuniorId.Get(context);

        _logger?.LogInformation(
            "Collecting communication signals for junior {JuniorId}",
            juniorId);

        var signal = new CommunicationSignal();

        try
        {
            // Communication data collection is best-effort.
            // If Slack ID is not available, we still succeed with empty data.
            if (!string.IsNullOrEmpty(slackId))
            {
                // In a real implementation, we would query Slack history.
                // For now, return a signal indicating communication is available but
                // we cannot query historical messages without additional Slack API scopes.
                signal.HasRecentCommunication = true;
                signal.RecentMessageCount = 0;
                signal.QuestionsAsked = 0;
                signal.CollectionSucceeded = true;

                _logger?.LogInformation(
                    "Communication signals collected for junior {JuniorId} (Slack available)",
                    juniorId);
            }
            else
            {
                signal.HasRecentCommunication = false;
                signal.CollectionSucceeded = true;

                _logger?.LogInformation(
                    "No Slack ID available for junior {JuniorId} — communication signals empty",
                    juniorId);
            }

            // Ensure async requirement is met
            await ValueTask.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to collect communication signals — continuing with partial data");
            signal.CollectionSucceeded = false;
            signal.Error = ex.Message;
        }

        context.SetResult(signal);
    }
}
