using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
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
///   - TimedOut: nobody decided within the approval SLA
///
/// <para><b>Durable approval SLA.</b> The gate had no timeout: an unanswered plan pinned
/// the instance in <c>Running</c> forever. It now arms a <c>context.DelayFor</c> bookmark
/// (EF-persisted, re-armed by <c>Elsa.Scheduling</c>'s startup task, so a host restart
/// inside the window does not drop it) at <c>Adl:PlanApprovalTimeoutMinutes</c>
/// (default 1440 = 24h) and completes <c>TimedOut</c> with a
/// <c>PLAN_APPROVAL.DECISION.TIMED_OUT</c> DCB event. A timeout is a REJECTION of the
/// plan for routing purposes — never an implicit approval — so a parent that has not yet
/// wired the <c>TimedOut</c> edge still cannot auto-proceed on an unanswered gate.</para>
///
/// <para><b>Not currently in any workflow graph.</b> No workflow constructs this activity
/// today (the cycle's human gate is the merge-approval sub-workflow), so the SLA is armed
/// for whoever wires the plan gate rather than fixing a live hang. Recorded here so the
/// next wiring does not have to rediscover it.</para>
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
[FlowNode("Approved", "Rejected", "EditRequested", "TimedOut")]
public class WaitForPlanApprovalActivity : Activity
{
    /// <summary>Config key for the plan-approval SLA.</summary>
    public const string TimeoutConfigKey = "Adl:PlanApprovalTimeoutMinutes";

    /// <summary>
    /// Default plan-approval SLA in minutes when <see cref="TimeoutConfigKey"/> is unset.
    /// 24h, matching <see cref="WaitForMergeApprovalActivity.DefaultTimeoutMinutes"/> —
    /// one working day, so an overnight or weekend review never trips it spuriously.
    /// </summary>
    public const int DefaultTimeoutMinutes = 1440;

    private readonly ILogger<WaitForPlanApprovalActivity>? _logger;
    private readonly IConfiguration? _configuration;

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

    public WaitForPlanApprovalActivity(
        ILogger<WaitForPlanApprovalActivity> logger,
        IConfiguration? configuration = null)
    {
        _logger = logger;
        _configuration = configuration;
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

        // Durable approval SLA — a DelayFor (Delay) bookmark, NOT the in-memory
        // IWorkflowScheduler: Elsa.Scheduling's startup task re-arms it after a host
        // restart, which an in-process timer would silently lose inside a 24h window.
        var configuration = _configuration ?? context.GetService<IConfiguration>();
        var slaMinutes = Math.Max(1,
            configuration?.GetValue<int?>(TimeoutConfigKey) ?? DefaultTimeoutMinutes);
        context.DelayFor(TimeSpan.FromMinutes(slaMinutes), OnTimeoutAsync);

        _logger?.LogInformation(
            "Plan approval gate armed for issue #{IssueNumber}; durable SLA at +{SlaMinutes}min",
            issueNumber, slaMinutes);
    }

    /// <summary>
    /// Durable timeout path: nobody decided within the SLA. Emits a distinct
    /// <c>PLAN_APPROVAL.DECISION.TIMED_OUT</c> error-status event (so "nobody looked" is
    /// not filed as "a human said no") and takes the <c>TimedOut</c> edge. The recorded
    /// decision is <see cref="ApprovalDecision.Reject"/> — fail-closed, so a parent that
    /// reads the result JSON can never treat an expired gate as an approval.
    /// </summary>
    private async ValueTask OnTimeoutAsync(ActivityExecutionContext context)
    {
        var issueNumber = IssueNumber.Get(context);
        var tenantId = PlanApprovalEvents.ParseTenantId(TenantId.GetOrDefault(context));
        const string feedback = "no plan decision was made within the approval SLA";

        var result = new ApprovalResult
        {
            Decision = ApprovalDecision.Reject,
            Feedback = feedback,
        };
        ApprovalResultJson.Set(context, System.Text.Json.JsonSerializer.Serialize(result));
        EditedPlanJson.Set(context, (string?)null);

        _logger?.LogWarning(
            "Plan approval SLA expired (durable timeout) for issue #{IssueNumber} — taking the TimedOut edge",
            issueNumber);

        TammaEventEmitter.Emit(context, this, _logger,
            BuildTammaEvent(
                PlanApprovalEvents.DecisionTimedOut, issueNumber, tenantId,
                decision: "timed_out", approvedBy: null, feedback: feedback));

        await context.CompleteActivityWithOutcomesAsync("TimedOut");
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
