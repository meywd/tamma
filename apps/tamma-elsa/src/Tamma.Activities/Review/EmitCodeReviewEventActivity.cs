using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.Review;

/// <summary>
/// Emits a <c>CODE_REVIEW.*</c> DCB event (completeness audit 2026-06-22,
/// <c>CodeReview.md</c> §Missing #8, Story 7-1D AC10) for the <c>code-review</c>
/// sub-workflow by appending a <see cref="TammaEvent"/> to the workflow's
/// <c>tamma:events</c> transient list via <see cref="TammaEventEmitter.Emit"/>. The merged
/// engine event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that
/// list <i>durably</i> to the tenant <c>domain_events</c> store after this activity runs —
/// the same pattern <see cref="Tamma.Activities.Blocker.EmitBlockerEventActivity"/> and
/// <see cref="Tamma.Activities.ADL.EmitBranchEventActivity"/> use. No activity holds a
/// DB / repository dependency of its own (none is registered in the Elsa engine — a
/// directly injected <c>IEventRepository</c> would be inert and silently drop every event).
/// The existing <c>MentorshipEvent</c> rows are kept in addition (they feed the mentorship
/// state machine); these events feed the platform audit/time-travel stream.
/// </summary>
[Activity(
    "Tamma.Review",
    "Emit Code Review Event",
    "Emit a CODE_REVIEW.* DCB event for the code-review audit trail",
    Kind = ActivityKind.Task
)]
public class EmitCodeReviewEventActivity : Activity
{
    private readonly ILogger<EmitCodeReviewEventActivity>? _logger;

    [Input(Description = "Event type — CODE_REVIEW.PR_CREATED.SUCCESS / .FAILED / .GUIDANCE_DELIVERED.SUCCESS / .FAILED / .ITERATION.STARTED / .MERGED.SUCCESS / .FAILED / .ESCALATED / .FAILED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Mentorship session id")]
    public Input<string?> SessionId { get; set; } = new((string?)null);

    [Input(Description = "Story id under review")]
    public Input<string?> StoryId { get; set; } = new((string?)null);

    [Input(Description = "Junior developer id")]
    public Input<string?> JuniorId { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Pull request number (0 = not yet created)")]
    public Input<int> PrNumber { get; set; } = new(0);

    [Input(Description = "Pull request URL")]
    public Input<string?> PrUrl { get; set; } = new((string?)null);

    [Input(Description = "Fix iteration number (ITERATION.STARTED / GUIDANCE events)")]
    public Input<int> Iteration { get; set; } = new(0);

    [Input(Description = "Merge commit sha (MERGED.SUCCESS events)")]
    public Input<string?> MergeSha { get; set; } = new((string?)null);

    [Input(Description = "Escalation reason (ESCALATED events)")]
    public Input<string?> Reason { get; set; } = new((string?)null);

    [Input(Description = "Error / failure detail (FAILED events)")]
    public Input<string?> Detail { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitCodeReviewEventActivity() { }

    public EmitCodeReviewEventActivity(ILogger<EmitCodeReviewEventActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.Get(context) ?? CodeReviewEvents.Failed;
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var juniorId = JuniorId.Get(context);
        var tenantId = CodeReviewEvents.ParseTenantId(TenantId.Get(context));
        var prNumber = PrNumber.Get(context);
        var prUrl = PrUrl.Get(context);
        var iteration = Iteration.Get(context);
        var mergeSha = MergeSha.Get(context);
        var reason = Reason.Get(context);
        var detail = Detail.Get(context);

        var evt = BuildTammaEvent(
            type, sessionId, storyId, juniorId, tenantId,
            prNumber, prUrl, iteration, mergeSha, reason, detail);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for session {Session} story {Story} (pr=#{Pr}, iteration={Iteration})",
            type, sessionId, storyId, prNumber, iteration);

        return default;
    }

    /// <summary>
    /// Map the code-review event inputs onto a <see cref="TammaEvent"/> expressed as the
    /// engine's transient-list event so the merged drain persists it. Tags carry the
    /// queryable DCB index keys (<c>sessionId</c>/<c>storyId</c>/<c>juniorId</c>/<c>prId</c>/
    /// <c>iteration</c>/<c>tenantId</c>); <c>Data</c> carries the per-milestone payload.
    /// Status is driven off the event type (<see cref="CodeReviewEvents.StatusForEvent"/>)
    /// so a creation/merge/guidance failure or rejection is a LOUD error row, never a false
    /// success. Pure (no Elsa context); exposed for unit testing the mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string? sessionId,
        string? storyId,
        string? juniorId,
        Guid? tenantId,
        int prNumber,
        string? prUrl,
        int iteration,
        string? mergeSha,
        string? reason,
        string? detail)
    {
        var tags = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(sessionId)) tags["sessionId"] = sessionId;
        if (!string.IsNullOrWhiteSpace(storyId)) tags["storyId"] = storyId;
        if (!string.IsNullOrWhiteSpace(juniorId)) tags["juniorId"] = juniorId;
        if (prNumber > 0) tags["prId"] = prNumber.ToString();
        if (iteration > 0) tags["iteration"] = iteration.ToString();
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>();
        if (prNumber > 0) data["prNumber"] = prNumber;
        if (!string.IsNullOrWhiteSpace(prUrl)) data["prUrl"] = prUrl;
        if (iteration > 0) data["iteration"] = iteration;
        if (!string.IsNullOrWhiteSpace(mergeSha)) data["mergeSha"] = mergeSha;
        if (!string.IsNullOrWhiteSpace(reason)) data["reason"] = reason;
        if (!string.IsNullOrWhiteSpace(detail)) data["detail"] = detail;

        var status = CodeReviewEvents.StatusForEvent(type);
        return new TammaEvent
        {
            EventType = type,
            Status = status,
            Error = status == "error" ? detail : null,
            Tags = tags,
            Data = data,
        };
    }
}
