using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Bookmark-based activity that blocks until a PR is approved.
/// Resumed by a GitHub webhook (pull_request_review.submitted with state=approved)
/// or by polling the PR status.
///
/// <para><b>Retained — not currently wired into <c>single-issue-cycle</c>.</b> The
/// binary approve/merge gate this activity drove was replaced by the richer
/// merge/test/reject <c>merge-approval</c> gate (<see cref="WaitForMergeApprovalActivity"/>).
/// It is kept for the <c>pull_request_review</c> webhook-resume seam: the merge gate
/// resumes on the tenant+repo-scoped
/// <c>adl-merge-approval-{tenant}-{repo}-{issue}-{pr}</c> bookmark via the
/// <c>POST /api/adl/merge-approval/resume</c> resume endpoint, and a future
/// webhook → <c>{decision}</c> mapping can reuse this activity's binary
/// <c>pr-approval-{pr}</c> bookmark shape if the platform ever drives the gate
/// straight from a review event. Do not delete without retiring that seam.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Wait for PR Approval",
    "Block until the pull request receives an approved review",
    Kind = ActivityKind.Task
)]
public class WaitForPRApprovalActivity : TammaOutcomeActivity
{
    public override string? EventType => "CYCLE.PR.APPROVAL.WAIT";

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "PR number")]
    public Input<int> PRNumber { get; set; } = default!;

    [Output(Description = "Who approved")]
    public Output<string?> ApprovedBy { get; set; } = default!;

    [JsonConstructor]
    public WaitForPRApprovalActivity() { }

    public WaitForPRApprovalActivity(ILogger<WaitForPRApprovalActivity> logger)
    {
        Logger = logger;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var prNumber = PRNumber.Get(context);

        Logger?.LogInformation("Waiting for PR #{PRNumber} approval...", prNumber);

        // Create bookmark — will be resumed when approval webhook arrives
        context.CreateBookmark(new CreateBookmarkArgs
        {
            Callback = OnApprovalReceived,
            BookmarkName = $"pr-approval-{prNumber}",
            IncludeActivityInstanceId = false,
        });
    }

    private async ValueTask OnApprovalReceived(ActivityExecutionContext context)
    {
        var approvedBy = context.WorkflowInput.GetValueOrDefault("approvedBy")?.ToString();
        ApprovedBy.Set(context, approvedBy);

        Logger?.LogInformation("PR #{PRNumber} approved by {ApprovedBy}",
            PRNumber.Get(context), approvedBy ?? "unknown");

        await context.CompleteActivityAsync();
    }

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["prNumber"] = PRNumber.Get(context),
        ["repository"] = Repository.Get(context),
    };

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["prNumber"] = PRNumber.Get(context),
        ["approvedBy"] = this.GetOutput<string?>(context, nameof(ApprovedBy)),
    };
}
