using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.Ambiguity;

/// <summary>
/// Story 3.6 — emits an <c>AMBIGUITY.*</c> DCB event for the <c>ambiguity-scoring</c>
/// sub-workflow by appending a <see cref="TammaEvent"/> to the workflow's <c>tamma:events</c>
/// transient list via <see cref="TammaEventEmitter.Emit"/>. The merged engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that list <i>durably</i> to
/// the tenant <c>domain_events</c> store — the same pattern
/// <see cref="Tamma.Activities.Research.EmitResearchEventActivity"/> and
/// <see cref="Tamma.Activities.Clarify.EmitClarifyEventActivity"/> use. No activity holds a
/// DB / repository dependency of its own (none is registered in the Elsa engine — a directly
/// injected repository would be inert and silently drop every event).
/// </summary>
[Activity(
    "Tamma.Ambiguity",
    "Emit Ambiguity Event",
    "Emit an AMBIGUITY.* DCB event for the ambiguity-scoring workflow audit trail",
    Kind = ActivityKind.Task
)]
public class EmitAmbiguityEventActivity : Activity
{
    private readonly ILogger<EmitAmbiguityEventActivity>? _logger;

    [Input(Description = "Event type — AMBIGUITY.STARTED / .SCORED / .CLARIFICATION_TRIGGERED / .BELOW_THRESHOLD / .FAILED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Scoring session id (unguessable Guid string)")]
    public Input<string?> SessionId { get; set; } = new((string?)null);

    [Input(Description = "Issue / requirement id being scored")]
    public Input<string?> IssueId { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Ambiguity score (0..1); null when not yet computed (started / failed)")]
    public Input<double?> Score { get; set; } = new((double?)null);

    [Input(Description = "Number of itemised ambiguities detected")]
    public Input<int> AmbiguityCount { get; set; } = new(0);

    [Input(Description = "Scorer confidence (0..1) in the assessment")]
    public Input<double> Confidence { get; set; } = new(0d);

    [Input(Description = "Clarify threshold (0..1) the score was compared against (decision events)")]
    public Input<double> Threshold { get; set; } = new(0d);

    [Input(Description = "Free-text detail for the audit payload")]
    public Input<string?> Detail { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitAmbiguityEventActivity() { }

    public EmitAmbiguityEventActivity(ILogger<EmitAmbiguityEventActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.Get(context) ?? AmbiguityEvents.Failed;
        var sessionId = SessionId.Get(context);
        var issueId = IssueId.Get(context);
        var tenantId = AmbiguityEvents.ParseTenantId(TenantId.Get(context));
        var score = Score.Get(context);
        var ambiguityCount = AmbiguityCount.Get(context);
        var confidence = Confidence.Get(context);
        var threshold = Threshold.Get(context);
        var detail = Detail.Get(context);

        var evt = BuildTammaEvent(type, sessionId, issueId, tenantId, score, ambiguityCount, confidence, threshold, detail);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for ambiguity session {Session} issue {Issue} (score={Score})",
            type, sessionId, issueId, score);

        return default;
    }

    /// <summary>
    /// Map the ambiguity event inputs onto a <see cref="TammaEvent"/> expressed as the engine's
    /// transient-list event so the merged drain persists it. Tags carry the queryable DCB index
    /// keys (<c>sessionId</c>/<c>issueId</c>/<c>tenantId</c>); <c>Data</c> carries the
    /// per-transition payload (<c>score</c>/<c>ambiguityCount</c>/<c>confidence</c>/
    /// <c>threshold</c>/<c>detail</c>). Status is driven off the event type
    /// (<see cref="AmbiguityEvents.StatusForEvent"/>) so a failed terminal is a LOUD error row,
    /// never a false success.
    ///
    /// <para><c>score</c> is a NULLABLE input so a genuine score of <c>0.0</c> (a perfectly
    /// clear requirement) is still recorded, while a not-yet-computed score (started / failed)
    /// is omitted — a plain <c>&gt; 0</c> guard could not tell those apart.</para>
    ///
    /// Pure (no Elsa context); exposed for unit testing the mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string? sessionId,
        string? issueId,
        Guid? tenantId,
        double? score,
        int ambiguityCount,
        double confidence,
        double threshold,
        string? detail)
    {
        var tags = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(sessionId)) tags["sessionId"] = sessionId;
        if (!string.IsNullOrWhiteSpace(issueId)) tags["issueId"] = issueId;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>();
        if (score is not null) data["score"] = score.Value;
        if (ambiguityCount > 0) data["ambiguityCount"] = ambiguityCount;
        if (confidence > 0d) data["confidence"] = confidence;
        if (threshold > 0d) data["threshold"] = threshold;
        if (!string.IsNullOrWhiteSpace(detail)) data["detail"] = detail;

        return new TammaEvent
        {
            EventType = type,
            Status = AmbiguityEvents.StatusForEvent(type),
            Tags = tags,
            Data = data,
        };
    }
}
