using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL.Models;
using Tamma.Activities.Core;

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
///
/// <para>Events (Story 4-6): emits <c>PLAN_APPROVAL.REQUESTED</c> at the RAISE point
/// (when the gate suspends on its bookmark) and a <c>PLAN_APPROVAL.DECISION.*</c> event on
/// resume, via <c>TammaEventEmitter.Emit</c> into the durable engine event drain — the same
/// path <see cref="EmitMergeApprovalEventActivity"/> uses. This keeps the plan-approval gate
/// auditable on the DCB stream (request + decision) consistent with the merge / deploy
/// approval gates.</para>
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

    [Input(Description = "Generated plan JSON to present for approval", UIHint = "json-editor")]
    public Input<string> PlanJson { get; set; } = default!;

    /// <summary>Tenant id (Story 4-6 — empty / single-user → platform-scope approval event).</summary>
    [Input(Description = "Tenant id (empty / single-user → platform-scope approval event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

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
        var tenantId = PlanApprovalEvents.ParseTenantId(TenantId.GetOrDefault(context));
        var bookmarkName = $"adl-plan-approval-{issueNumber}";

        _logger?.LogInformation(
            "Creating plan approval bookmark {BookmarkName} for issue #{IssueNumber}",
            bookmarkName, issueNumber);

        // Story 4-6 — emit PLAN_APPROVAL.REQUESTED at the RAISE point so the approval request
        // is on the DCB audit stream the moment the gate suspends (mirrors the merge / deploy
        // approval-request events). The decision is emitted on resume below.
        TammaEventEmitter.Emit(context, this, _logger,
            BuildTammaEvent(PlanApprovalEvents.Requested, issueNumber, tenantId,
                decision: null, approvedBy: null, feedback: null));

        context.CreateBookmark(
            new CreateBookmarkArgs
            {
                BookmarkName = bookmarkName,
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

        // Story 4-6 — emit the human decision as a PLAN_APPROVAL.DECISION.* DCB event on the
        // resuming edge so the approver / feedback context is captured durably (a rejection is
        // a LOUD error-status row, never a silent approve).
        var issueNumber = IssueNumber.Get(context);
        var tenantId = PlanApprovalEvents.ParseTenantId(TenantId.GetOrDefault(context));
        TammaEventEmitter.Emit(context, this, _logger,
            BuildTammaEvent(
                PlanApprovalEvents.DecisionEventType(result.Decision),
                issueNumber, tenantId,
                decision: result.Decision.ToString().ToLowerInvariant(),
                approvedBy: result.ApprovedBy,
                feedback: feedback));

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

    /// <summary>
    /// Map the plan-approval gate inputs onto a <see cref="TammaEvent"/> expressed as the
    /// engine's transient-list event so the merged drain persists it. Tags carry the
    /// queryable DCB index keys (<c>issueId</c>/<c>issueNumber</c>/<c>tenantId</c>/
    /// <c>decision</c>/<c>approver</c>); <c>Data</c> carries the decision payload. Status is
    /// driven off the event type (a rejection is a LOUD error row). Pure (no Elsa context) —
    /// exposed for unit testing the mapping. Mirrors
    /// <see cref="EmitMergeApprovalEventActivity.BuildTammaEvent"/>.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        int issueNumber,
        Guid? tenantId,
        string? decision,
        string? approvedBy,
        string? feedback)
    {
        var tags = new Dictionary<string, object?>
        {
            ["issueId"] = issueNumber.ToString(),
            ["issueNumber"] = issueNumber.ToString(),
        };
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");
        if (!string.IsNullOrWhiteSpace(decision)) tags["decision"] = decision;
        if (!string.IsNullOrWhiteSpace(approvedBy)) tags["approver"] = approvedBy;

        var data = new Dictionary<string, object?>
        {
            ["decision"] = decision ?? "",
            ["approver"] = approvedBy ?? "",
            ["feedback"] = feedback ?? "",
        };

        return new TammaEvent
        {
            EventType = type,
            Status = PlanApprovalEvents.StatusForEvent(type),
            Tags = tags,
            Data = data,
        };
    }
}
