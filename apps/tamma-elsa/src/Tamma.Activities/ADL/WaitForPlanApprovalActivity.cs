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
/// Bookmark-based activity that suspends the workflow until a human approves,
/// rejects, or requests edits to the AI-generated plan.
///
/// Resume via API: POST /api/adl/{instanceId}/plan-approval
///   body: { "decision": "approve|reject|edit", "feedback": "...", "editedPlan": "..." }
///
/// Outcomes:
///   - Approved: plan accepted
///   - Rejected: plan rejected, cycle should end
///   - EditRequested: plan needs revision, loop back to plan generation
/// </summary>
[Activity(
    "Tamma.ADL",
    "Wait For Plan Approval",
    "Suspend workflow and wait for human approval of the generated plan",
    Kind = ActivityKind.Task
)]
[FlowNode("Approved", "Rejected", "EditRequested")]
public class WaitForPlanApprovalActivity : Activity
{
    private readonly ILogger<WaitForPlanApprovalActivity>? _logger;

    [Input(Description = "Issue number for bookmark identification")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Generated plan JSON to present for approval")]
    public Input<string> PlanJson { get; set; } = default!;

    [Output(Description = "Approval result with decision and feedback")]
    public Output<string?> ApprovalResultJson { get; set; } = default!;

    [Output(Description = "Edited plan JSON if decision was EditRequested")]
    public Output<string?> EditedPlanJson { get; set; } = default!;

    [JsonConstructor]
    public WaitForPlanApprovalActivity() { }

    public WaitForPlanApprovalActivity(ILogger<WaitForPlanApprovalActivity> logger)
    {
        _logger = logger;
    }

    protected override void Execute(ActivityExecutionContext context)
    {
        var issueNumber = IssueNumber.Get(context);
        var bookmarkName = $"adl-plan-approval-{issueNumber}";

        _logger?.LogInformation(
            "Creating plan approval bookmark {BookmarkName} for issue #{IssueNumber}",
            bookmarkName, issueNumber);

        context.CreateBookmark(
            new CreateBookmarkArgs
            {
                BookmarkName = bookmarkName,
                Payload = new { IssueNumber = issueNumber },
                Callback = OnApprovalReceivedAsync,
                AutoBurn = true
            });
    }

    private async ValueTask OnApprovalReceivedAsync(ActivityExecutionContext context)
    {
        var input = context.WorkflowInput;

        var decisionStr = input.TryGetValue("decision", out var d) ? d?.ToString() : null;
        var feedback = input.TryGetValue("feedback", out var f) ? f?.ToString() : null;
        var editedPlan = input.TryGetValue("editedPlan", out var e) ? e?.ToString() : null;

        var result = new ApprovalResult
        {
            Decision = ParseDecision(decisionStr),
            Feedback = feedback,
            EditedPlan = editedPlan,
            ApprovedBy = input.TryGetValue("approvedBy", out var a) ? a?.ToString() : null
        };

        var resultJson = System.Text.Json.JsonSerializer.Serialize(result);
        ApprovalResultJson.Set(context, resultJson);
        EditedPlanJson.Set(context, editedPlan);

        _logger?.LogInformation(
            "Plan approval received for issue #{IssueNumber}: {Decision}",
            IssueNumber.Get(context), result.Decision);

        var outcome = result.Decision switch
        {
            ApprovalDecision.Approve => "Approved",
            ApprovalDecision.Reject => "Rejected",
            ApprovalDecision.Edit => "EditRequested",
            _ => "Rejected"
        };

        await context.CompleteActivityWithOutcomesAsync(outcome);
    }

    private static ApprovalDecision ParseDecision(string? decision) => decision?.ToLowerInvariant() switch
    {
        "approve" => ApprovalDecision.Approve,
        "reject" => ApprovalDecision.Reject,
        "edit" => ApprovalDecision.Edit,
        _ => ApprovalDecision.Reject
    };
}
