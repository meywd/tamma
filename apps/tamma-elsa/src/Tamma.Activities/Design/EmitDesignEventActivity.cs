using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.Design;

/// <summary>
/// Story 3.7 — emits a <c>DESIGN.*</c> DCB event for the <c>design-proposal</c>
/// sub-workflow by appending a <see cref="TammaEvent"/> to the workflow's
/// <c>tamma:events</c> transient list via <see cref="TammaEventEmitter.Emit"/>. The merged
/// engine event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that
/// list <i>durably</i> to the tenant <c>domain_events</c> store — the same pattern
/// <see cref="Tamma.Activities.Clarify.EmitClarifyEventActivity"/> and
/// <see cref="Tamma.Activities.ADL.EmitBranchEventActivity"/> use. No activity holds a DB /
/// repository dependency of its own (none is registered in the Elsa engine — a directly
/// injected repository would be inert and silently drop every event).
/// </summary>
[Activity(
    "Tamma.Design",
    "Emit Design Event",
    "Emit a DESIGN.* DCB event for the design-proposal audit trail",
    Kind = ActivityKind.Task
)]
public class EmitDesignEventActivity : Activity
{
    private readonly ILogger<EmitDesignEventActivity>? _logger;

    [Input(Description = "Event type — DESIGN.PROPOSAL.GENERATED / .DELIVERED / .APPROVED / .REJECTED / .FAILED / DESIGN.REVIEW.TIMED_OUT")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Design session id (unguessable Guid string)")]
    public Input<string?> SessionId { get; set; } = new((string?)null);

    [Input(Description = "Issue / requirement id the design is for")]
    public Input<string?> IssueId { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Delivery channel (issue-comment / api) for DELIVERED events")]
    public Input<string?> Channel { get; set; } = new((string?)null);

    [Input(Description = "Number of design alternatives weighed in the proposal")]
    public Input<int> AlternativeCount { get; set; } = new(0);

    [Input(Description = "Free-text detail for the audit payload (review feedback / failure reason)")]
    public Input<string?> Detail { get; set; } = new((string?)null);

    [Input(Description = "Reviewer identity for APPROVED / REJECTED decisions (non-repudiation)")]
    public Input<string?> Reviewer { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitDesignEventActivity() { }

    public EmitDesignEventActivity(ILogger<EmitDesignEventActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.Get(context) ?? DesignEvents.ProposalFailed;
        var sessionId = SessionId.Get(context);
        var issueId = IssueId.Get(context);
        var tenantId = DesignEvents.ParseTenantId(TenantId.Get(context));
        var channel = Channel.Get(context);
        var alternativeCount = AlternativeCount.Get(context);
        var detail = Detail.Get(context);
        var reviewer = Reviewer.Get(context);

        var evt = BuildTammaEvent(type, sessionId, issueId, tenantId, channel, alternativeCount, detail, reviewer);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for design session {Session} issue {Issue} (alternatives={Count})",
            type, sessionId, issueId, alternativeCount);

        return default;
    }

    /// <summary>
    /// Map the design event inputs onto a <see cref="TammaEvent"/> expressed as the engine's
    /// transient-list event so the merged drain persists it. Tags carry the queryable DCB
    /// index keys (<c>sessionId</c>/<c>issueId</c>/<c>channel</c>/<c>tenantId</c>);
    /// <c>Data</c> carries the per-transition payload (alternative count, detail, reviewer).
    /// Status is driven off the event type (<see cref="DesignEvents.StatusForEvent"/>) so a
    /// failed/timed-out terminal is a LOUD error row, never a false success. Pure (no Elsa
    /// context); exposed for unit testing the mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string? sessionId,
        string? issueId,
        Guid? tenantId,
        string? channel,
        int alternativeCount,
        string? detail,
        string? reviewer)
    {
        var tags = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(sessionId)) tags["sessionId"] = sessionId;
        if (!string.IsNullOrWhiteSpace(issueId)) tags["issueId"] = issueId;
        if (!string.IsNullOrWhiteSpace(channel)) tags["channel"] = channel;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>();
        if (alternativeCount > 0) data["alternativeCount"] = alternativeCount;
        if (!string.IsNullOrWhiteSpace(detail)) data["detail"] = detail;
        if (!string.IsNullOrWhiteSpace(reviewer)) data["reviewer"] = reviewer;

        return new TammaEvent
        {
            EventType = type,
            Status = DesignEvents.StatusForEvent(type),
            Tags = tags,
            Data = data,
        };
    }
}
