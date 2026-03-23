using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.ADL;

/// <summary>
/// Selects the next unassigned GitHub issue matching the configured labels.
/// Assigns it to the bot user and outputs the issue details.
///
/// Outcomes:
///   - Selected: an issue was found and assigned
///   - NoIssues: no matching unassigned issues remain
///   - Error: GitHub API call failed
/// </summary>
[Activity(
    "Tamma.ADL",
    "Select Issue",
    "Query GitHub for the next unassigned issue matching configured labels",
    Kind = ActivityKind.Task
)]
[FlowNode("Selected", "NoIssues", "Error")]
public class SelectIssueActivity : Activity
{
    private readonly ILogger<SelectIssueActivity>? _logger;
    private readonly IGitHubIntegrationService? _github;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Labels to filter issues by")]
    public Input<string[]> IssueLabels { get; set; } = default!;

    [Input(Description = "Bot assignee username")]
    public Input<string> BotAssignee { get; set; } = default!;

    [Output(Description = "Selected issue details as JSON")]
    public Output<string?> IssueJson { get; set; } = default!;

    [Output(Description = "Selected issue number")]
    public Output<int> IssueNumber { get; set; } = default!;

    [Output(Description = "Selected issue title")]
    public Output<string?> IssueTitle { get; set; } = default!;

    [JsonConstructor]
    public SelectIssueActivity() { }

    public SelectIssueActivity(
        ILogger<SelectIssueActivity> logger,
        IGitHubIntegrationService github)
    {
        _logger = logger;
        _github = github;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context);
        var labels = IssueLabels.Get(context);
        var botAssignee = BotAssignee.Get(context);

        try
        {
            var result = await _github!.ListGitHubIssuesAsync(repository, labels);
            if (!result.Success)
            {
                _logger?.LogError("Failed to list issues: {Error}", result.Error);
                await context.CompleteActivityWithOutcomesAsync("Error");
                return;
            }

            // Find first unassigned issue
            var issue = result.Data?.FirstOrDefault(i => string.IsNullOrEmpty(i.Assignee));
            if (issue == null)
            {
                _logger?.LogInformation("No unassigned issues found for labels [{Labels}]",
                    string.Join(", ", labels));
                await context.CompleteActivityWithOutcomesAsync("NoIssues");
                return;
            }

            // Assign to bot
            await _github.AssignGitHubIssueAsync(repository, issue.Number, botAssignee);

            var adlIssue = new AdlIssue
            {
                Number = issue.Number,
                Title = issue.Title,
                Body = issue.Body,
                Labels = issue.Labels,
                Url = issue.Url,
                CreatedAt = issue.CreatedAt
            };

            var json = System.Text.Json.JsonSerializer.Serialize(adlIssue);
            IssueJson.Set(context, json);
            IssueNumber.Set(context, issue.Number);
            IssueTitle.Set(context, issue.Title);

            _logger?.LogInformation("Selected issue #{Number}: {Title}", issue.Number, issue.Title);
            await context.CompleteActivityWithOutcomesAsync("Selected");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error selecting issue from {Repository}", repository);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}
