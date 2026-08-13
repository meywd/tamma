using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-6 — emits a <c>DOCUMENT.*</c> DCB event for the generic
/// <c>document-lifecycle</c> workflow by appending a <see cref="TammaEvent"/> to
/// the workflow's <c>tamma:events</c> transient list via
/// <see cref="TammaEventEmitter.Emit"/>. The merged engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that list
/// <i>durably</i> to the tenant <c>domain_events</c> store — the same pattern
/// <see cref="Tamma.Activities.Decomposition.EmitDecompositionEventActivity"/>
/// uses. No activity holds a DB / repository dependency of its own.
///
/// <para>Tags carry the queryable DCB index keys (<c>issueId</c> / <c>documentId</c>
/// / <c>documentType</c> / <c>round</c> / <c>correlationId</c> / <c>sessionId</c> /
/// <c>tenantId</c>); <c>Data</c> carries the per-transition payload
/// (<c>detail</c> + optional <c>dataJson</c>). The event STATUS is driven off the
/// event type (<see cref="DocumentEvents.StatusForEvent"/>) so a FAILED / REJECTED
/// / ESCALATED terminal is a LOUD error row, never a false success.</para>
/// </summary>
[Activity(
    "Tamma.Documents",
    "Emit Document Event",
    "Emit a DOCUMENT.* DCB event for the generic document-lifecycle workflow audit trail",
    Kind = ActivityKind.Task
)]
public class EmitDocumentEventActivity : Activity
{
    private readonly ILogger<EmitDocumentEventActivity>? _logger;

    [Input(Description = "Event type — DOCUMENT.PRODUCED.SUCCESS / .VALIDATED.FAILED / .REVIEWED / .ACCEPTED / …")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Document id under transition (unguessable Guid string)")]
    public Input<string?> DocumentId { get; set; } = new((string?)null);

    [Input(Description = "Document type key (a DocumentTypeKey wire string)")]
    public Input<string?> DocumentType { get; set; } = new((string?)null);

    [Input(Description = "Revision round (0 on the first produce)")]
    public Input<int> Round { get; set; } = new(0);

    [Input(Description = "Issue / requirement id (lineage anchor)")]
    public Input<string?> IssueId { get; set; } = new((string?)null);

    [Input(Description = "Correlation id (lineage anchor)")]
    public Input<string?> CorrelationId { get; set; } = new((string?)null);

    [Input(Description = "Decision-session id (unguessable Guid string)")]
    public Input<string?> SessionId { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Free-text detail for the audit payload")]
    public Input<string?> Detail { get; set; } = new((string?)null);

    [Input(Description = "Optional structured JSON payload (e.g. outcome / lineage summary)")]
    public Input<string?> DataJson { get; set; } = new((string?)null);

    // Story 39-11 (Design Decision D6) — additive, optional. When set to a valid
    // Guid string, it becomes the emitted TammaEvent.Id so the durable
    // domain_events row carries the SAME id the store stamps as
    // correlating_event_id — the AC7 store↔stream linkage. Unset (the pre-39-11
    // behaviour) leaves the auto-minted per-event id untouched. Purely additive:
    // no existing tag/data mapping or event structure changes.
    [Input(Description = "Optional pre-minted event id (Guid string) — the AC7 store↔stream linkage")]
    public Input<string?> EventId { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitDocumentEventActivity() { }

    public EmitDocumentEventActivity(ILogger<EmitDocumentEventActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.GetOrDefault(context) ?? DocumentEvents.Escalated;
        var documentId = DocumentId.GetOrDefault(context);
        var documentType = DocumentType.GetOrDefault(context);
        var round = Round.GetOrDefault(context);
        var issueId = IssueId.GetOrDefault(context);
        var correlationId = CorrelationId.GetOrDefault(context);
        var sessionId = SessionId.GetOrDefault(context);
        var tenantId = DocumentEvents.ParseTenantId(TenantId.GetOrDefault(context));
        var detail = Detail.GetOrDefault(context);
        var dataJson = DataJson.GetOrDefault(context);

        var evt = BuildTammaEvent(
            type, documentId, documentType, round, issueId, correlationId, sessionId, tenantId, detail, dataJson);

        // D6 — override the auto-minted id with the pre-minted transition event id
        // when supplied, so the store's correlating_event_id resolves to THIS row.
        var eventId = EventId.GetOrDefault(context);
        if (!string.IsNullOrWhiteSpace(eventId) && Guid.TryParse(eventId, out var preMinted))
            evt.Id = preMinted;

        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for document {Doc} ({DocType}) round {Round} issue {Issue}",
            type, documentId, documentType, round, issueId);

        await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete (see EmitEscalationEventActivity precedent); without this the workflow hangs here forever
        return;
    }

    /// <summary>
    /// Map the document event inputs onto a <see cref="TammaEvent"/>. Pure (no Elsa
    /// context); exposed for unit testing the tag/data mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string? documentId,
        string? documentType,
        int round,
        string? issueId,
        string? correlationId,
        string? sessionId,
        Guid? tenantId,
        string? detail,
        string? dataJson)
    {
        var tags = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(issueId)) tags["issueId"] = issueId;
        if (!string.IsNullOrWhiteSpace(documentId)) tags["documentId"] = documentId;
        if (!string.IsNullOrWhiteSpace(documentType)) tags["documentType"] = documentType;
        tags["round"] = round;
        if (!string.IsNullOrWhiteSpace(correlationId)) tags["correlationId"] = correlationId;
        if (!string.IsNullOrWhiteSpace(sessionId)) tags["sessionId"] = sessionId;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(detail)) data["detail"] = detail;
        if (!string.IsNullOrWhiteSpace(dataJson)) data["payload"] = dataJson;

        return new TammaEvent
        {
            EventType = type,
            Status = DocumentEvents.StatusForEvent(type),
            Tags = tags,
            Data = data,
        };
    }
}
