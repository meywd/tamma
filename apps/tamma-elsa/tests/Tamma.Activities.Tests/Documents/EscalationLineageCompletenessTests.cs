using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Documents;

namespace Tamma.Activities.Tests.Documents;

/// <summary>
/// Story 39-8 (AC6). Every field of the 39-6 document lineage must survive into the
/// <c>ESCALATION.TRIGGERED</c> event's <c>lineage</c> data — a handler reconstructs the whole
/// story from the one event, never a bare failure string.
///
/// <para><b>Substitution note.</b> The plan specifies reflecting over 39-6's
/// <c>DocumentLineage</c> wire properties to prove no field is dropped. 39-6 is NOT landed yet
/// (the type does not exist), so this test instead uses a REPRESENTATIVE lineage JSON object
/// string and asserts (a) it is embedded VERBATIM (every property round-trips) into the event's
/// <c>lineage</c> field and (b) the payload is a JSON OBJECT, never a bare string. When 39-6
/// lands, swap the representative string for reflection over <c>DocumentLineage</c>.</para>
/// </summary>
[TestFixture]
public class EscalationLineageCompletenessTests
{
    // A representative shape of 39-6's DocumentLineage: draft envelope ids+states, review ids
    // (member reviews for panels), rounds used, last domain-phrased violations, the typed
    // outcome, and the effective policy reference.
    private const string RepresentativeLineageJson = """
    {
      "drafts": [
        { "id": "draft-1", "state": "superseded" },
        { "id": "draft-2", "state": "reviewed" }
      ],
      "reviews": [
        { "id": "review-1", "reviewer": "panel-member-a", "decision": "request-changes" },
        { "id": "review-2", "reviewer": "panel-member-b", "decision": "approve" }
      ],
      "roundsUsed": 2,
      "lastViolations": [ "the API contract omits pagination", "no rollback path documented" ],
      "outcome": "rounds-exhausted",
      "policyReference": "system-default@3"
    }
    """;

    [Test]
    public void TriggeredEvent_EmbedsLineageVerbatim_EveryFieldSurvives()
    {
        var evt = EmitEscalationEventActivity.BuildTammaEvent(
            ApprovalEvents.EscalationTriggered, "esc-1", "rounds-exhausted",
            lineageJson: RepresentativeLineageJson, rulesReference: "system-default@3", channel: "orchestrator",
            issueId: "issue-9", documentId: "doc-7", documentType: "design", correlationId: "corr-5",
            sessionId: "sess-3", tenantId: null, detail: null);

        evt.Data["lineage"].Should().BeAssignableTo<JsonNode>();
        var embedded = (JsonNode)evt.Data["lineage"]!;
        embedded.GetValueKind().Should().Be(JsonValueKind.Object,
            "the lineage must be a JSON object, never a bare failure string");

        // Reflect over the SOURCE lineage's every wire property and assert each appears in the
        // embedded lineage (no field dropped — survives future lineage additions).
        var source = JsonNode.Parse(RepresentativeLineageJson)!.AsObject();
        foreach (var (key, _) in source)
        {
            embedded.AsObject().ContainsKey(key).Should().BeTrue(
                $"lineage field '{key}' must survive into the escalation event");
        }

        // Deep equality — the whole story round-trips.
        JsonNode.DeepEquals(embedded, source).Should().BeTrue(
            "the embedded lineage must equal the source lineage byte-for-byte (verbatim embedding)");
    }

    [Test]
    public void TriggeredEvent_LineageIsNeverABareString()
    {
        var evt = EmitEscalationEventActivity.BuildTammaEvent(
            ApprovalEvents.EscalationTriggered, "esc-1", "rounds-exhausted",
            lineageJson: RepresentativeLineageJson, rulesReference: null, channel: null,
            issueId: null, documentId: null, documentType: null, correlationId: null,
            sessionId: null, tenantId: null, detail: null);

        (evt.Data["lineage"] is string).Should().BeFalse(
            "the lineage payload must never be a bare string (AC6)");
    }
}
