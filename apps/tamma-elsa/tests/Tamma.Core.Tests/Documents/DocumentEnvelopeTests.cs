using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents;

namespace Tamma.Core.Tests.Documents;

/// <summary>
/// Tests for the <see cref="DocumentEnvelope"/> core (Story 39-2 AC1 + the AC2
/// enforcement seam): UUID v7 identity, strict producer provenance, lineage
/// guards, and mutation-free state transitions.
/// </summary>
[TestFixture]
public class DocumentEnvelopeTests
{
    private static JsonElement Payload(string json = "{\"ok\":true}")
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static DocumentProducer ValidProducer() =>
        DocumentProducer.Create("senior_developer", "decompose-issue", "issue-decomposition");

    private static DocumentEnvelope Draft() =>
        DocumentEnvelope.CreateDraft(
            DocumentTypeKey.Decomposition, 1, "issue-42", "corr-1", ValidProducer(), Payload());

    // -----------------------------------------------------------------------
    // Identity — UUID v7
    // -----------------------------------------------------------------------

    [Test]
    public void CreateDraft_mints_a_version_7_id()
    {
        var envelope = Draft();
        var bytes = envelope.Id.ToByteArray(bigEndian: true);
        (bytes[6] >> 4).Should().Be(7, "the UUID version nibble must be 7");
    }

    [Test]
    public void CreateDraft_ids_are_time_ordered()
    {
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddMilliseconds(5);

        var first = DocumentEnvelope.CreateDraft(
            DocumentTypeKey.Decomposition, 1, "i", "c", ValidProducer(), Payload(), now: t1);
        var second = DocumentEnvelope.CreateDraft(
            DocumentTypeKey.Decomposition, 1, "i", "c", ValidProducer(), Payload(), now: t2);

        // Big-endian UUID v7 sorts in creation order both as string and bytes.
        string.CompareOrdinal(first.Id.ToString(), second.Id.ToString()).Should().BeLessThan(0);
    }

    [Test]
    public void CreateDraft_starts_in_draft_with_equal_timestamps()
    {
        var envelope = Draft();
        envelope.State.Should().Be(DocumentState.Draft);
        envelope.CreatedAt.Should().Be(envelope.UpdatedAt);
    }

    // -----------------------------------------------------------------------
    // Lineage guards
    // -----------------------------------------------------------------------

    [Test]
    [TestCase("")]
    [TestCase("   ")]
    public void CreateDraft_throws_on_empty_issue_id(string issueId)
    {
        var act = () => DocumentEnvelope.CreateDraft(
            DocumentTypeKey.Decomposition, 1, issueId, "corr-1", ValidProducer(), Payload());
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.ENVELOPE.INVALID");
    }

    [Test]
    [TestCase("")]
    [TestCase("   ")]
    public void CreateDraft_throws_on_empty_correlation_id(string correlationId)
    {
        var act = () => DocumentEnvelope.CreateDraft(
            DocumentTypeKey.Decomposition, 1, "issue-42", correlationId, ValidProducer(), Payload());
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.ENVELOPE.INVALID");
    }

    // -----------------------------------------------------------------------
    // Producer provenance — strict role/action, structural workflow id
    // -----------------------------------------------------------------------

    [Test]
    public void Producer_Create_accepts_a_taxonomy_valid_triple()
    {
        var producer = DocumentProducer.Create("senior_developer", "decompose-issue", "issue-decomposition");
        producer.Role.Should().Be("senior_developer");
        producer.Action.Should().Be("decompose-issue");
        producer.WorkflowDefinitionId.Should().Be("issue-decomposition");
    }

    [Test]
    public void Producer_Create_throws_on_unknown_role()
    {
        var act = () => DocumentProducer.Create("wizard", "decompose-issue", "issue-decomposition");
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.PRODUCER.INVALID");
    }

    [Test]
    public void Producer_Create_throws_on_unknown_action()
    {
        var act = () => DocumentProducer.Create("senior_developer", "cast-spell", "issue-decomposition");
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.PRODUCER.INVALID");
    }

    [Test]
    public void Producer_Create_throws_on_ineligible_pair()
    {
        // tech_writer is not eligible for decompose-issue (a senior_developer action).
        var act = () => DocumentProducer.Create("tech_writer", "decompose-issue", "issue-decomposition");
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.PRODUCER.INVALID");
    }

    [Test]
    public void Producer_Create_throws_on_malformed_workflow_id()
    {
        var act = () => DocumentProducer.Create("senior_developer", "decompose-issue", "Not Kebab");
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.PRODUCER.INVALID");
    }

    // -----------------------------------------------------------------------
    // Immutability + transition seam
    // -----------------------------------------------------------------------

    [Test]
    public void WithState_returns_a_new_instance_leaving_the_original_unchanged()
    {
        var draft = DocumentEnvelope.CreateDraft(
            DocumentTypeKey.Decomposition, 1, "i", "c", ValidProducer(), Payload(),
            now: DateTimeOffset.UtcNow);

        var validated = draft.WithState(DocumentState.Validated, now: draft.CreatedAt.AddMilliseconds(10));

        ReferenceEquals(draft, validated).Should().BeFalse();
        draft.State.Should().Be(DocumentState.Draft, "the original envelope must be unmutated");
        validated.State.Should().Be(DocumentState.Validated);
        validated.UpdatedAt.Should().BeAfter(draft.UpdatedAt);
        validated.Id.Should().Be(draft.Id, "a state transition keeps the same identity");
    }

    [Test]
    public void WithState_rejects_an_illegal_transition_naming_both_states()
    {
        var draft = Draft();
        var act = () => draft.WithState(DocumentState.Accepted);
        var error = act.Should().Throw<TammaError>().Which;
        error.Code.Should().Be("DOCUMENT.STATE.ILLEGAL_TRANSITION");
        error.Message.Should().Contain("draft").And.Contain("accepted");
    }
}
