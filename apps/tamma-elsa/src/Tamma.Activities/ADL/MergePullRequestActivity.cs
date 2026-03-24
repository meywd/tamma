using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.ADL;

/// <summary>
/// Squash-merges the pull request, closes the issue, and deletes the feature branch.
///
/// Outcomes:
///   - Merged: PR merged, issue closed, branch deleted
///   - Error: merge or cleanup failed
/// </summary>
[Activity(
    "Tamma.ADL",
    "Merge Pull Request",
    "Squash-merge PR, close issue, and delete feature branch",
    Kind = ActivityKind.Task
)]
[FlowNode("Merged", "Error")]
public class MergePullRequestActivity : Activity
{
    private readonly ILogger<MergePullRequestActivity>? _logger;
    private readonly IGitHubIntegrationService? _github;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Pull request number")]
    public Input<int> PrNumber { get; set; } = default!;

    [Input(Description = "Issue number to close")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Branch to delete after merge")]
    public Input<string> BranchName { get; set; } = default!;

    [Output(Description = "Merge commit SHA")]
    public Output<string?> MergeSha { get; set; } = default!;

    [JsonConstructor]
    public MergePullRequestActivity() { }

    public MergePullRequestActivity(
        ILogger<MergePullRequestActivity> logger,
        IGitHubIntegrationService github)
    {
        _logger = logger;
        _github = github;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context);
        var prNumber = PrNumber.Get(context);
        var issueNumber = IssueNumber.Get(context);
        var branchName = BranchName.Get(context);

        try
        {
            // 1. Squash-merge the PR
            var mergeResult = await _github!.MergeGitHubPullRequestAsync(repository, prNumber);
            if (!mergeResult.Success)
            {
                _logger?.LogError("Failed to merge PR #{Number}: {Error}",
                    prNumber, mergeResult.Error);
                await context.CompleteActivityWithOutcomesAsync("Error");
                return;
            }

            MergeSha.Set(context, mergeResult.Data?.MergeSha);

            // 2. Close the issue with a comment
            var comment = $"Resolved by PR #{prNumber} (merge SHA: {mergeResult.Data?.MergeSha})";
            await _github.CloseGitHubIssueAsync(repository, issueNumber, comment);

            // 3. Delete the feature branch (best-effort)
            try
            {
                await _github.DeleteGitHubBranchAsync(repository, branchName);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete branch {Branch} (non-fatal)", branchName);
            }

            _logger?.LogInformation(
                "Merged PR #{PrNumber}, closed issue #{IssueNumber}, deleted branch {Branch}",
                prNumber, issueNumber, branchName);

            await context.CompleteActivityWithOutcomesAsync("Merged");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error merging PR #{Number}", prNumber);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}
