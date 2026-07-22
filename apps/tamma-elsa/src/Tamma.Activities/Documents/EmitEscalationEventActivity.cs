using System.Text.Json;
using System.Text.Json.Nodes;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.Core;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-8 (AC1/AC6) — emits an <c>ESCALATION.*</c> DCB event for the document
/// lifecycle's escalated exit region by appending a <see cref="TammaEvent"/> to the
/// workflow's <c>tamma:events</c> transient list via
/// <see cref="TammaEventEmitter.Emit"/>; the merged engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes it DURABLY to the
/// tenant <c>domain_events</c> store — the same pattern
/// <see cref="Tamma.Activities.Decomposition.EmitDecompositionEventActivity"/> uses.
/// No activity holds a DB / repository dependency of its own.
///
/// <para><b>D9 — one emit site.</b> All four unhandleable outcomes AND every
/// always-escalate hit route through 39-6's escalated terminal, so one node here
/// covers AC1's both clauses. The event embeds the FULL serialized 39-6 document
/// lineage (AC6) as a nested JSON object — never a bare failure string — plus the
/// typed outcome name, the effective policy reference, and a freshly minted
/// <c>escalationId</c> the disposition surface pairs on.</para>
/// </summary>
[Activity(
    "Tamma.Documents",
    "Emit Escalation Event",
    "Emit an ESCALATION.* DCB event (with full document lineage) for the escalation exception surface",
    Kind = ActivityKind.Task
)]
public class EmitEscalationEventActivity : Activity
{
    private readonly ILogger<EmitEscalationEventActivity>? _logger;

    [Input(Description = "Event type — ESCALATION.TRIGGERED / .RESOLVED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Escalation id (unguessable Guid string — the pairing key)")]
    public Input<string?> EscalationId { get; set; } = new((string?)null);

    [Input(Description = "Typed outcome / escalate-reason wire name")]
    public Input<string?> Outcome { get; set; } = new((string?)null);

    [Input(Description = "Serialized 39-6 DocumentLineage (embedded verbatim as a JSON object)")]
    public Input<string?> LineageJson { get; set; } = new((string?)null);

    [Input(Description = "Resolved acceptance-rules reference")]
    public Input<string?> RulesReference { get; set; } = new((string?)null);

    [Input(Description = "Transport channel (orchestrator|user|api)")]
    public Input<string?> Channel { get; set; } = new((string?)null);

    [Input(Description = "Issue / requirement id")]
    public Input<string?> IssueId { get; set; } = new((string?)null);

    [Input(Description = "Document id under escalation")]
    public Input<string?> DocumentId { get; set; } = new((string?)null);

    [Input(Description = "Document type key")]
    public Input<string?> DocumentType { get; set; } = new((string?)null);

    [Input(Description = "Correlation id")]
    public Input<string?> CorrelationId { get; set; } = new((string?)null);

    [Input(Description = "Decision-session id")]
    public Input<string?> SessionId { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Free-text detail for the audit payload")]
    public Input<string?> Detail { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitEscalationEventActivity() { }

    public EmitEscalationEventActivity(ILogger<EmitEscalationEventActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.Get(context) ?? ApprovalEvents.EscalationTriggered;

        var evt = BuildTammaEvent(
            type,
            EscalationId.Get(context),
            Outcome.Get(context),
            LineageJson.Get(context),
            RulesReference.Get(context),
            Channel.Get(context),
            IssueId.Get(context),
            DocumentId.Get(context),
            DocumentType.Get(context),
            CorrelationId.Get(context),
            SessionId.Get(context),
            ApprovalEvents.ParseTenantId(TenantId.Get(context)),
            Detail.Get(context));

        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for escalation {EscalationId} (outcome={Outcome}, document={DocumentId})",
            type, EscalationId.Get(context), Outcome.Get(context), DocumentId.Get(context));

        // Complete on the default outcome so a mid-flow emit node continues to the next activity
        // (the escalated exit region wires this before its terminal). A plain Activity does not
        // auto-complete, so this explicit completion is required.
        await context.CompleteActivityAsync();
    }

    /// <summary>
    /// Map the escalation inputs onto a <see cref="TammaEvent"/>. Tags carry the queryable DCB
    /// index keys (<c>issueId</c>/<c>documentId</c>/<c>documentType</c>/<c>correlationId</c>/
    /// <c>escalationId</c>/<c>sessionId</c>/<c>tenantId</c>); <c>Data</c> carries the exception
    /// payload — the typed <c>outcome</c>, the full <c>lineage</c> embedded as a nested JSON
    /// object (AC6 — NEVER a bare string), the <c>rulesReference</c>, the <c>channel</c>, and any
    /// <c>detail</c>. Status is driven off the type (<see cref="ApprovalEvents.StatusForEvent"/>)
    /// so a TRIGGERED is a LOUD error row. Pure (no Elsa context); exposed for unit testing.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string? escalationId,
        string? outcome,
        string? lineageJson,
        string? rulesReference,
        string? channel,
        string? issueId,
        string? documentId,
        string? documentType,
        string? correlationId,
        string? sessionId,
        Guid? tenantId,
        string? detail)
    {
        var tags = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(issueId)) tags["issueId"] = issueId;
        if (!string.IsNullOrWhiteSpace(documentId)) tags["documentId"] = documentId;
        if (!string.IsNullOrWhiteSpace(documentType)) tags["documentType"] = documentType;
        if (!string.IsNullOrWhiteSpace(correlationId)) tags["correlationId"] = correlationId;
        if (!string.IsNullOrWhiteSpace(escalationId)) tags["escalationId"] = escalationId;
        if (!string.IsNullOrWhiteSpace(sessionId)) tags["sessionId"] = sessionId;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>
        {
            // AC6 — the lineage is embedded as a nested JSON OBJECT, never a bare string, so a
            // handler can reconstruct the whole story from the one event.
            ["lineage"] = EmbedLineage(lineageJson),
        };
        if (!string.IsNullOrWhiteSpace(outcome)) data["outcome"] = outcome;
        if (!string.IsNullOrWhiteSpace(rulesReference)) data["rulesReference"] = rulesReference;
        if (!string.IsNullOrWhiteSpace(channel)) data["channel"] = channel;
        if (!string.IsNullOrWhiteSpace(detail)) data["detail"] = detail;

        return new TammaEvent
        {
            EventType = type,
            Status = ApprovalEvents.StatusForEvent(type),
            Tags = tags,
            Data = data,
        };
    }

    /// <summary>Parse the serialized lineage into a <see cref="JsonNode"/> so it serializes as a
    /// nested object; an unparseable/empty payload is wrapped in an object (never surfaced as a
    /// bare string) so AC6's "never a bare failure string" holds even on malformed input.</summary>
    private static JsonNode EmbedLineage(string? lineageJson)
    {
        if (!string.IsNullOrWhiteSpace(lineageJson))
        {
            try
            {
                if (JsonNode.Parse(lineageJson) is JsonNode node)
                    return node;
            }
            catch (JsonException)
            {
                // fall through to the wrapped-object form
            }
        }

        return new JsonObject { ["_unparsed"] = lineageJson };
    }
}
