using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Bookmark-based activity that blocks until a PR is merged.
/// Resumed by a GitHub webhook (pull_request.closed with merged=true).
/// </summary>
[Activity(
    "Tamma.ADL",
    "Wait for PR Merged",
    "Block until the pull request is merged",
    Kind = ActivityKind.Task
)]
public class WaitForPRMergedActivity : TammaOutcomeActivity
{
    public override string? EventType => "CYCLE.PR.MERGE.WAIT";

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "PR number")]
    public Input<int> PRNumber { get; set; } = default!;

    [Output(Description = "Merge commit SHA")]
    public Output<string?> MergeSha { get; set; } = default!;

    [JsonConstructor]
    public WaitForPRMergedActivity() { }

    public WaitForPRMergedActivity(ILogger<WaitForPRMergedActivity> logger)
    {
        Logger = logger;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var prNumber = PRNumber.Get(context);
        Logger?.LogInformation("Waiting for PR #{PRNumber} to be merged...", prNumber);

        context.CreateBookmark(new CreateBookmarkArgs
        {
            Callback = OnMerged,
            BookmarkName = $"pr-merged-{prNumber}",
            IncludeActivityInstanceId = false,
        });
    }

    private async ValueTask OnMerged(ActivityExecutionContext context)
    {
        var sha = context.WorkflowInput.GetValueOrDefault("mergeSha")?.ToString();
        MergeSha.Set(context, sha);

        Logger?.LogInformation("PR #{PRNumber} merged. SHA: {MergeSha}",
            PRNumber.Get(context), sha ?? "unknown");

        await context.CompleteActivityAsync();
    }

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["prNumber"] = PRNumber.Get(context),
        ["mergeSha"] = this.GetOutput<string?>(context, nameof(MergeSha)),
    };
}
