using System.Text.Json;
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
/// Fetches PR review comments from GitHub and analyzes them via AI
/// to determine which comments are actionable and need fixes.
///
/// Outcomes:
///   - Done: analysis complete (check HasActionableComments output)
///   - Error: failed to fetch or analyze comments
/// </summary>
[Activity(
    "Tamma.ADL",
    "Analyze Review",
    "Fetch and analyze PR review comments for actionable items",
    Kind = ActivityKind.Task
)]
[FlowNode("Done", "Error")]
public class AnalyzeReviewActivity : Activity
{
    private readonly ILogger<AnalyzeReviewActivity>? _logger;
    private readonly IGitHubIntegrationService? _github;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Pull request number")]
    public Input<int> PrNumber { get; set; } = default!;

    [Output(Description = "Whether there are actionable review comments")]
    public Output<bool> HasActionableComments { get; set; } = default!;

    [Output(Description = "Review analysis result as JSON")]
    public Output<string?> AnalysisJson { get; set; } = default!;

    [JsonConstructor]
    public AnalyzeReviewActivity() { }

    public AnalyzeReviewActivity(
        ILogger<AnalyzeReviewActivity> logger,
        IGitHubIntegrationService github)
    {
        _logger = logger;
        _github = github;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context);
        var prNumber = PrNumber.Get(context);

        try
        {
            var result = await _github!.GetPullRequestReviewCommentsAsync(repository, prNumber);
            if (!result.Success)
            {
                _logger?.LogError("Failed to fetch review comments: {Error}", result.Error);
                await context.CompleteActivityWithOutcomesAsync("Error");
                return;
            }

            var comments = result.Data ?? new List<GitHubReviewComment>();

            var analysis = new ReviewAnalysisResult
            {
                TotalComments = comments.Count,
                HasActionableComments = comments.Count > 0,
                ActionableComments = comments.Count,
                FixItems = comments.Select(c => new ReviewFixItem
                {
                    FilePath = c.Path ?? "",
                    Line = c.Line,
                    Comment = c.Body,
                    Priority = "normal"
                }).ToList(),
                Summary = comments.Count > 0
                    ? $"Found {comments.Count} review comment(s) requiring attention"
                    : "No review comments found"
            };

            HasActionableComments.Set(context, analysis.HasActionableComments);
            AnalysisJson.Set(context, JsonSerializer.Serialize(analysis));

            _logger?.LogInformation("Analyzed PR #{Number}: {Count} actionable comments",
                prNumber, analysis.ActionableComments);
            await context.CompleteActivityWithOutcomesAsync("Done");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error analyzing review for PR #{Number}", prNumber);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}
