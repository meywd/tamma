using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents;

namespace Tamma.Core.Tests.Documents;

/// <summary>
/// Story 41-1c D2/C7 — the <see cref="DocumentEnvelope.Audience"/> field:
/// payload → envelope copy on the draft-mint path, the explicit-vs-payload
/// mismatch guard, JSON round-trip, <c>WithState</c> preservation, and the C7
/// regression pin: <see cref="DocumentEnvelope"/> has a HAND-WRITTEN
/// <c>Equals</c>/<c>GetHashCode</c>, so a new member left out of them would be
/// silently excluded from equality — two envelopes differing only in audience
/// must NOT be equal.
/// </summary>
[TestFixture]
public class DocumentEnvelopeAudienceTests
{
    private static JsonElement Payload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static DocumentProducer Producer() =>
        DocumentProducer.Create("devops", "write-postmortem", "document-lifecycle");

    // Compact (no whitespace): envelope equality compares Payload by raw text,
    // and DocumentJson serialization writes compact JSON — a pretty-printed
    // payload would round-trip value-identical but raw-text different.
    private const string ProsePayload =
        """{"kind":"postmortem","audience":"engineering","title":"The outage","body":"## What happened\nWords."}""";

    private static DocumentEnvelope ProseDraft(string? audience = null, DateTimeOffset? now = null) =>
        DocumentEnvelope.CreateDraft(
            DocumentTypeKey.Prose, 1, "issue-1", "corr-1", Producer(), Payload(ProsePayload),
            audience: audience, now: now);

    // ── The D2 copy path ─────────────────────────────────────────────────────

    [Test]
    public void CreateDraft_copies_the_payload_audience_onto_the_envelope()
    {
        ProseDraft().Audience.Should().Be("engineering",
            "the draft-mint path copies payload → envelope so the store can filter without parsing bodies");
    }

    [Test]
    public void CreateDraft_with_matching_explicit_audience_succeeds()
    {
        ProseDraft(audience: "engineering").Audience.Should().Be("engineering");
    }

    [Test]
    public void CreateDraft_with_disagreeing_explicit_audience_fails_loud()
    {
        var act = () => ProseDraft(audience: "stakeholder");
        act.Should().Throw<TammaError>().Which.Code.Should().Be("PROSE_AUDIENCE_ENVELOPE_MISMATCH");
    }

    [Test]
    public void CreateDraft_without_a_payload_audience_leaves_the_envelope_audience_null()
    {
        // Every non-prose document: no audience key in the payload, no tag on the
        // envelope, and NO existing call site changes.
        var envelope = DocumentEnvelope.CreateDraft(
            DocumentTypeKey.Decomposition, 1, "issue-1", "corr-1",
            DocumentProducer.Create("senior_developer", "decompose-issue", "issue-decomposition"),
            Payload("""{ "summary": "s", "subtasks": [] }"""));
        envelope.Audience.Should().BeNull();
    }

    // ── Round-trip + state transitions ───────────────────────────────────────

    [Test]
    public void Audience_survives_a_DocumentJson_round_trip()
    {
        var original = ProseDraft();
        var roundTripped = DocumentJson.Deserialize(DocumentJson.Serialize(original));
        roundTripped.Audience.Should().Be("engineering");
        roundTripped.Should().Be(original, "the round-tripped envelope is value-equal to the original");
    }

    [Test]
    public void WithState_preserves_the_audience()
    {
        var draft = ProseDraft(now: DateTimeOffset.UtcNow);
        draft.WithState(DocumentState.Validated, draft.CreatedAt.AddMilliseconds(5))
            .Audience.Should().Be("engineering");
    }

    // ── The C7 regression pin: hand-written equality must see the new member ──

    [Test]
    public void Envelopes_differing_only_in_audience_are_not_equal_and_hash_differently()
    {
        var one = ProseDraft(now: DateTimeOffset.Parse("2026-07-29T00:00:00.000Z"));
        var other = one with { Audience = "ops" };

        one.Equals(other).Should().BeFalse(
            "Equals is hand-written (JsonElement breaks record equality) — Audience must be compared");
        one.GetHashCode().Should().NotBe(other.GetHashCode());
    }
}
