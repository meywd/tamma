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
/// Bookmark-based activity that suspends the workflow until a human
/// decides whether to merge, run more tests, or reject the PR.
///
/// Resume via API: POST /api/adl/{instanceId}/merge-approval
///   body: { "decision": "merge|test|reject", "feedback": "..." }
///
/// Outcomes:
///   - Merge: approved to merge
///   - Test: run additional tests before merging
///   - Reject: PR rejected
/// </summary>
[Activity(
    "Tamma.ADL",
    "Wait For Merge Approval",
    "Suspend workflow and wait for merge/test/reject decision",
    Kind = ActivityKind.Task
)]
[FlowNode("Merge", "Test", "Reject")]
public class WaitForMergeApprovalActivity : Activity
{
    private readonly ILogger<WaitForMergeApprovalActivity>? _logger;

    [Input(Description = "Issue number for bookmark identification")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "PR number")]
    public Input<int> PrNumber { get; set; } = default!;

    [Input(Description = "PR URL")]
    public Input<string?> PrUrl { get; set; } = default!;

    [Output(Description = "Approval decision")]
    public Output<string?> Decision { get; set; } = default!;

    [Output(Description = "Feedback from reviewer")]
    public Output<string?> Feedback { get; set; } = default!;

    [JsonConstructor]
    public WaitForMergeApprovalActivity() { }

    public WaitForMergeApprovalActivity(ILogger<WaitForMergeApprovalActivity> logger)
    {
        _logger = logger;
    }

    protected override void Execute(ActivityExecutionContext context)
    {
        var issueNumber = IssueNumber.Get(context);
        var prNumber = PrNumber.Get(context);
        var bookmarkName = $"adl-merge-approval-{issueNumber}-{prNumber}";

        _logger?.LogInformation(
            "Creating merge approval bookmark {BookmarkName} for PR #{PrNumber}",
            bookmarkName, prNumber);

        context.CreateBookmark(
            new CreateBookmarkArgs
            {
                BookmarkName = bookmarkName,
                Callback = OnMergeDecisionAsync,
                AutoBurn = true
            });
    }

    private async ValueTask OnMergeDecisionAsync(ActivityExecutionContext context)
    {
        var input = context.WorkflowInput;

        var decisionStr = input.TryGetValue("decision", out var d) ? d?.ToString() : null;
        var feedback = input.TryGetValue("feedback", out var f) ? f?.ToString() : null;

        Decision.Set(context, decisionStr);
        Feedback.Set(context, feedback);

        _logger?.LogInformation(
            "Merge decision received for PR #{PrNumber}: {Decision}",
            PrNumber.Get(context), decisionStr);

        var outcome = decisionStr?.ToLowerInvariant() switch
        {
            "merge" => "Merge",
            "test" => "Test",
            "reject" => "Reject",
            _ => "Reject"
        };

        await context.CompleteActivityWithOutcomesAsync(outcome);
    }
}
