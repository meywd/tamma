using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Emits a <c>MERGE_APPROVAL.*</c> / <c>MERGE.*</c> DCB event (FR-19 / FR-34 /
/// Story 4-6) for the audit trail by appending a <see cref="TammaEvent"/> to the
/// workflow's <c>tamma:events</c> transient list via
/// <see cref="TammaEventEmitter.Emit"/>. The engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that list
/// <i>durably</i> to the tenant <c>domain_events</c> store after this activity
/// runs — the drain resolves the tenant from the workflow scope (the
/// merge-approval workflow stamps a <c>TenantId</c> variable). The event
/// therefore persists without this activity holding any DB / repository
/// dependency of its own (none is registered in the Elsa engine — the same
/// reason <see cref="EmitPrEventActivity"/> uses the emitter rather than a direct
/// <c>IEventRepository</c>).
///
/// <para>This is the explicit-decision-edge emitter: the bookmark activity
/// (<see cref="WaitForMergeApprovalActivity"/>, a <c>TammaOutcomeActivity</c>)
/// already auto-emits <c>APPROVAL.GATE.STARTED/.FAILED</c>, but the human
/// decision (merge / test / reject / invalid / escalate) lands on a distinct
/// graph edge and carries approver / feedback / breakingChange context — so each
/// edge emits its own DCB event through this activity.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Emit Merge Approval Event",
    "Emit a MERGE_APPROVAL.* / MERGE.* DCB event for the approval-gate audit trail",
    Kind = ActivityKind.Task
)]
public class EmitMergeApprovalEventActivity : Activity
{
    private readonly ILogger<EmitMergeApprovalEventActivity>? _logger;

    [Input(Description = "Event type — e.g. MERGE_APPROVAL.DECISION.MERGED / MERGE.REQUESTED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Issue number this PR closes")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Pull request number")]
    public Input<int> PrNumber { get; set; } = new(0);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Approval decision string (merge|test|reject|<invalid>)")]
    public Input<string?> Decision { get; set; } = new((string?)null);

    [Input(Description = "Approver identity captured from the resume payload")]
    public Input<string?> Approver { get; set; } = new((string?)null);

    [Input(Description = "Free-text reviewer feedback")]
    public Input<string?> Feedback { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitMergeApprovalEventActivity() { }

    public EmitMergeApprovalEventActivity(ILogger<EmitMergeApprovalEventActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.GetOrDefault(context) ?? MergeApprovalEvents.Escalated;
        var issueNumber = IssueNumber.GetOrDefault(context);
        var prNumber = PrNumber.GetOrDefault(context);
        var tenantId = MergeApprovalEvents.ParseTenantId(TenantId.GetOrDefault(context));
        var decision = Decision.GetOrDefault(context);
        var approver = Approver.GetOrDefault(context);
        var feedback = Feedback.GetOrDefault(context);

        var evt = BuildTammaEvent(type, issueNumber, prNumber, tenantId, decision, approver, feedback);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for issue #{Issue} pr #{Pr} (decision={Decision}, approver={Approver})",
            type, issueNumber, prNumber, decision ?? "", approver ?? "");

        await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete (see EmitEscalationEventActivity precedent); without this the workflow hangs here forever
        return;
    }

    /// <summary>
    /// Map the gate inputs onto a <see cref="TammaEvent"/> expressed as the
    /// engine's transient-list event so the merged drain persists it. Tags carry
    /// the queryable DCB index keys (<c>issueId</c>/<c>issueNumber</c>/
    /// <c>prNumber</c>/<c>tenantId</c>/<c>decision</c>/<c>approver</c>); Data
    /// carries the decision payload. Pure (no Elsa context) — exposed for unit
    /// testing the mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        int issueNumber,
        int prNumber,
        Guid? tenantId,
        string? decision,
        string? approver,
        string? feedback)
    {
        var tags = new Dictionary<string, object?>
        {
            ["issueId"] = issueNumber.ToString(),
            ["issueNumber"] = issueNumber.ToString(),
        };
        if (prNumber > 0) tags["prNumber"] = prNumber.ToString();
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");
        if (!string.IsNullOrWhiteSpace(decision)) tags["decision"] = decision;
        if (!string.IsNullOrWhiteSpace(approver)) tags["approver"] = approver;

        var data = new Dictionary<string, object?>
        {
            ["decision"] = decision ?? "",
            ["approver"] = approver ?? "",
            ["feedback"] = feedback ?? "",
        };

        return new TammaEvent
        {
            EventType = type,
            Status = IsFailureEvent(type) ? "error" : "success",
            Tags = tags,
            Data = data,
        };
    }

    /// <summary>
    /// Rejected / invalid / escalated / failed-merge transitions are loud
    /// (error-status) audit rows — they are NOT a false success. Merge / test /
    /// merge-requested are normal progress.
    /// </summary>
    public static bool IsFailureEvent(string type)
        => type is MergeApprovalEvents.DecisionRejected
                or MergeApprovalEvents.DecisionInvalid
                or MergeApprovalEvents.Escalated
                or MergeApprovalEvents.MergeFailed;
}
