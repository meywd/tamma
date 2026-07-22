using System.Globalization;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Core;
using Tamma.Activities.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Activities.Tests.Documents;

/// <summary>
/// Story 39-8 (AC3 — the pairing test). From a captured event list ALONE, a consumer must be
/// able to pair <c>REQUESTED↔PROVIDED</c> (via <c>correlationId</c>+<c>sessionId</c>) and
/// <c>TRIGGERED↔RESOLVED</c> (via <c>escalationId</c>) and recompute time-to-resolve, and that
/// recomputed duration must match the DENORMALIZED <c>durationMs</c> the resolving event
/// carries — so dashboards never need a stream join.
/// </summary>
[TestFixture]
public class ApprovalPairingReconstructionTests
{
    [Test]
    public void RequestedProvided_PairByCorrelationAndSession_ReconstructsDuration()
    {
        var session = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        var requestedAt = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        const long durationMs = 5_000;

        var requested = WaitForDocumentDecisionActivity.BuildRequestedEvent(
            session, tenant.ToString(), issueId: "issue-9", documentId: "doc-7",
            documentType: "design", correlationId: "corr-5", rulesReference: "system-default@1",
            requestedAtUtc: requestedAt.ToString("O", CultureInfo.InvariantCulture));

        var read = new WaitForDocumentDecisionActivity.DecisionReadResult(
            new AcceptanceDecision.Accept(), "{\"kind\":\"accept\"}", "Accept", "accept",
            Feedback: "ok", DeciderId: "alice@x.test", DeciderDisplay: "Alice", Channel: "user",
            RulesReference: "system-default@1");

        var provided = WaitForDocumentDecisionActivity.BuildProvidedEvent(
            session, tenant.ToString(), "issue-9", "doc-7", "design", "corr-5", "system-default@1",
            read, durationMs);
        // The PROVIDED event fires at requestedAt + durationMs.
        provided.Timestamp = requestedAt.UtcDateTime.AddMilliseconds(durationMs);

        var stream = new List<TammaEvent> { requested, provided };

        // Pair from the stream alone.
        var req = stream.Single(e => e.EventType == ApprovalEvents.Requested);
        var prov = stream.Single(e =>
            e.EventType == ApprovalEvents.Provided
            && (string?)e.Tags!["correlationId"] == (string?)req.Tags!["correlationId"]
            && (string?)e.Tags!["sessionId"] == (string?)req.Tags!["sessionId"]);

        var reqAt = DateTimeOffset.Parse((string)req.Data["requestedAtUtc"]!, CultureInfo.InvariantCulture);
        var reconstructed = (long)(new DateTimeOffset(prov.Timestamp, TimeSpan.Zero) - reqAt).TotalMilliseconds;

        reconstructed.Should().Be((long)prov.Data["durationMs"]!,
            "the denormalized durationMs must equal the duration recomputed from the paired REQUESTED event");
        reconstructed.Should().Be(durationMs);
    }

    [Test]
    public void TriggeredResolved_PairByEscalationId_ReconstructsDuration()
    {
        var triggeredAt = new DateTimeOffset(2026, 7, 22, 11, 0, 0, TimeSpan.Zero);
        const long durationMs = 12_000;
        const string escalationId = "esc-42";

        var triggered = EmitEscalationEventActivity.BuildTammaEvent(
            ApprovalEvents.EscalationTriggered, escalationId, outcome: "rounds-exhausted",
            lineageJson: "{\"roundsUsed\":3}", rulesReference: "system-default@1", channel: "orchestrator",
            issueId: "issue-9", documentId: "doc-7", documentType: "design", correlationId: "corr-5",
            sessionId: "sess-3", tenantId: null, detail: "rounds ran out");
        triggered.Timestamp = triggeredAt.UtcDateTime;

        // The RESOLVED event (appended by the Api disposition service) mirrors the escalationId tag
        // and carries the denormalized durationMs.
        var resolved = new TammaEvent
        {
            EventType = ApprovalEvents.EscalationResolved,
            Status = "success",
            Timestamp = triggeredAt.UtcDateTime.AddMilliseconds(durationMs),
            Tags = new Dictionary<string, object?> { ["escalationId"] = escalationId, ["correlationId"] = "corr-5" },
            Data = new Dictionary<string, object?> { ["disposition"] = "resolved", ["durationMs"] = durationMs },
        };

        var stream = new List<TammaEvent> { triggered, resolved };

        var trig = stream.Single(e => e.EventType == ApprovalEvents.EscalationTriggered);
        var res = stream.Single(e =>
            e.EventType == ApprovalEvents.EscalationResolved
            && (string?)e.Tags!["escalationId"] == (string?)trig.Tags!["escalationId"]);

        var reconstructed = (long)(res.Timestamp - trig.Timestamp).TotalMilliseconds;

        reconstructed.Should().Be((long)res.Data["durationMs"]!,
            "the RESOLVED durationMs must equal the duration recomputed from the paired TRIGGERED event");
        reconstructed.Should().Be(durationMs);
    }
}
