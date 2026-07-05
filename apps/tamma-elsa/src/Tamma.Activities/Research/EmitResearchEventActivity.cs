using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.Research;

/// <summary>
/// Story 3.4 — emits a <c>RESEARCH.*</c> DCB event for the <c>research</c> sub-workflow
/// by appending a <see cref="TammaEvent"/> to the workflow's <c>tamma:events</c> transient
/// list via <see cref="TammaEventEmitter.Emit"/>. The merged engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that list <i>durably</i>
/// to the tenant <c>domain_events</c> store — the same pattern
/// <see cref="Tamma.Activities.Clarify.EmitClarifyEventActivity"/> and
/// <see cref="Tamma.Activities.Blocker.EmitBlockerEventActivity"/> use. No activity holds a
/// DB / repository dependency of its own (none is registered in the Elsa engine — a
/// directly injected repository would be inert and silently drop every event).
/// </summary>
[Activity(
    "Tamma.Research",
    "Emit Research Event",
    "Emit a RESEARCH.* DCB event for the research workflow audit trail",
    Kind = ActivityKind.Task
)]
public class EmitResearchEventActivity : Activity
{
    private readonly ILogger<EmitResearchEventActivity>? _logger;

    [Input(Description = "Event type — RESEARCH.STARTED / .CONTEXT_GATHERED / .COMPLETED / .FAILED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Research session id (unguessable Guid string)")]
    public Input<string?> SessionId { get; set; } = new((string?)null);

    [Input(Description = "Issue / topic id the research is about")]
    public Input<string?> IssueId { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Number of ranked findings synthesized")]
    public Input<int> FindingCount { get; set; } = new(0);

    [Input(Description = "Overall confidence score (0..1) of the synthesized research")]
    public Input<double> Confidence { get; set; } = new(0d);

    [Input(Description = "Free-text detail for the audit payload")]
    public Input<string?> Detail { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitResearchEventActivity() { }

    public EmitResearchEventActivity(ILogger<EmitResearchEventActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.Get(context) ?? ResearchEvents.Failed;
        var sessionId = SessionId.Get(context);
        var issueId = IssueId.Get(context);
        var tenantId = ResearchEvents.ParseTenantId(TenantId.Get(context));
        var findingCount = FindingCount.Get(context);
        var confidence = Confidence.Get(context);
        var detail = Detail.Get(context);

        var evt = BuildTammaEvent(type, sessionId, issueId, tenantId, findingCount, confidence, detail);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for research session {Session} issue {Issue} (findings={Count})",
            type, sessionId, issueId, findingCount);

        return default;
    }

    /// <summary>
    /// Map the research event inputs onto a <see cref="TammaEvent"/> expressed as the
    /// engine's transient-list event so the merged drain persists it. Tags carry the
    /// queryable DCB index keys (<c>sessionId</c>/<c>issueId</c>/<c>tenantId</c>);
    /// <c>Data</c> carries the per-transition payload (<c>findingCount</c>/
    /// <c>confidence</c>/<c>detail</c>). Status is driven off the event type
    /// (<see cref="ResearchEvents.StatusForEvent"/>) so a failed terminal is a LOUD error
    /// row, never a false success. Pure (no Elsa context); exposed for unit testing the
    /// mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string? sessionId,
        string? issueId,
        Guid? tenantId,
        int findingCount,
        double confidence,
        string? detail)
    {
        var tags = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(sessionId)) tags["sessionId"] = sessionId;
        if (!string.IsNullOrWhiteSpace(issueId)) tags["issueId"] = issueId;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>();
        if (findingCount > 0) data["findingCount"] = findingCount;
        if (confidence > 0d) data["confidence"] = confidence;
        if (!string.IsNullOrWhiteSpace(detail)) data["detail"] = detail;

        return new TammaEvent
        {
            EventType = type,
            Status = ResearchEvents.StatusForEvent(type),
            Tags = tags,
            Data = data,
        };
    }
}
