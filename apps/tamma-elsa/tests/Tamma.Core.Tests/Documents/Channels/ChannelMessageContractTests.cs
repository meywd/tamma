using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Channels;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Documents.Channels;

/// <summary>
/// Story 39-18 (AC1) — the CLOSED channel message set is drift-tested: polymorphic
/// round-trip per kind with pinned <c>kind</c> strings, the derived-set count pin
/// (exactly 8), the <see cref="ChannelAudience"/> wire pins (exactly 2), envelope
/// round-trip fidelity, and the opaque lineage payload preserved through the wire.
/// </summary>
[TestFixture]
public class ChannelMessageContractTests
{
    private static readonly JsonSerializerOptions Options = DocumentJson.Options;

    // ── the eight pinned kinds ──────────────────────────────────────────────

    private static IEnumerable<TestCaseData> Kinds()
    {
        yield return new TestCaseData(new AcceptanceRequested(SampleRequest()), "acceptance-request");
        yield return new TestCaseData(new DecisionProvided(new AcceptanceDecision.Accept(), "orchestrator-agent", "sys@1"), "acceptance-decision");
        yield return new TestCaseData(new TaskAssigned(Guid.NewGuid(), Guid.NewGuid(), "senior_developer", "initiator", "decomposition", Guid.NewGuid(), "issue-1", 70, "sys@1"), "task-assigned");
        yield return new TestCaseData(new EscalationRaised("esc-1", "rounds-exhausted", """{"issueId":"issue-1"}""", "issue-1", "sys@1"), "escalation-raised");
        yield return new TestCaseData(new Tamma.Core.Documents.Channels.EscalationDisposition("esc-1", "resolved", "handled"), "escalation-disposition");
        yield return new TestCaseData(new GuidanceQuery(Guid.NewGuid(), "corr-1", "what now?", null), "guidance-query");
        yield return new TestCaseData(new GuidanceReply(Guid.NewGuid(), "do this"), "guidance-reply");
        yield return new TestCaseData(new AgentConversationMessage(Guid.NewGuid(), Guid.NewGuid(), "user->agent", "hi"), "agent-conversation");
    }

    [TestCaseSource(nameof(Kinds))]
    public void Message_RoundTrips_WithPinnedKind(ChannelMessage message, string expectedKind)
    {
        var json = JsonSerializer.Serialize<ChannelMessage>(message, Options);

        // The discriminator on the wire is the pinned kind string.
        var node = JsonNode.Parse(json)!;
        node["kind"]!.GetValue<string>().Should().Be(expectedKind);
        ChannelMessageKinds.KindOf(message).Should().Be(expectedKind);

        // Deserializes back to the SAME derived type, and re-serializes to the SAME
        // JSON (semantic round-trip — robust against record list-reference equality).
        var back = JsonSerializer.Deserialize<ChannelMessage>(json, Options);
        back.Should().BeOfType(message.GetType());
        var reserialized = JsonSerializer.Serialize<ChannelMessage>(back!, Options);
        JsonNode.DeepEquals(JsonNode.Parse(reserialized), JsonNode.Parse(json)).Should().BeTrue();
    }

    [Test]
    public void DerivedSet_IsClosed_ExactlyEight()
    {
        var derived = typeof(ChannelMessage)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .ToList();

        derived.Should().HaveCount(8, "the channel message set is closed at 8 kinds (AC1)");
        derived.Select(d => (string)d.TypeDiscriminator!).Should().BeEquivalentTo(new[]
        {
            "acceptance-request", "acceptance-decision", "task-assigned", "escalation-raised",
            "escalation-disposition", "guidance-query", "guidance-reply", "agent-conversation",
        });
    }

    [Test]
    public void ChannelAudience_WireValues_AreExactlyTwo()
    {
        Enum.GetValues<ChannelAudience>().Should().HaveCount(2);
        ChannelAudience.Orchestrator.ToWire().Should().Be("orchestrator");
        ChannelAudience.User.ToWire().Should().Be("user");
    }

    // ── envelope fidelity ───────────────────────────────────────────────────

    [Test]
    public void Envelope_RoundTrip_PreservesIdentityAndRecipient()
    {
        var recipient = Guid.NewGuid();
        var envelope = new ChannelEnvelope(
            MessageId: UuidV7.NewGuid(),
            TenantId: Guid.NewGuid(),
            Audience: ChannelAudience.User,
            RecipientUserId: recipient,
            Message: new TaskAssigned(Guid.NewGuid(), Guid.NewGuid(), "senior_developer", "repo-access", "decomposition", Guid.NewGuid(), "issue-9", 80, null),
            CreatedAt: DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(envelope, Options);
        var back = JsonSerializer.Deserialize<ChannelEnvelope>(json, Options)!;

        back.MessageId.Should().Be(envelope.MessageId);
        back.TenantId.Should().Be(envelope.TenantId);
        back.Audience.Should().Be(ChannelAudience.User);
        back.RecipientUserId.Should().Be(recipient);
        back.Message.Should().BeOfType<TaskAssigned>();
    }

    [Test]
    public void EscalationRaised_LineageJson_SurvivesTheWire()
    {
        const string lineage = """{"issueId":"issue-1","types":[],"unlinkedReviews":[],"outcome":"escalated"}""";
        var envelope = new ChannelEnvelope(
            UuidV7.NewGuid(), Guid.NewGuid(), ChannelAudience.Orchestrator, null,
            new EscalationRaised("esc-42", "always-escalate-class", lineage, "issue-1", null),
            DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(envelope, Options);
        var back = JsonSerializer.Deserialize<ChannelEnvelope>(json, Options)!;

        var raised = back.Message.Should().BeOfType<EscalationRaised>().Subject;
        JsonNode.DeepEquals(JsonNode.Parse(raised.LineageJson), JsonNode.Parse(lineage)).Should().BeTrue();
    }

    // ── audience map (server-derive-and-validate) ───────────────────────────

    [Test]
    public void AudienceFor_MapsKinds_AndRefusesConversation()
    {
        ChannelMessageKinds.AudienceFor("acceptance-request").Should().Be(ChannelAudience.Orchestrator);
        ChannelMessageKinds.AudienceFor("task-assigned").Should().Be(ChannelAudience.User);
        ChannelMessageKinds.AudienceFor("guidance-query").Should().Be(ChannelAudience.Orchestrator);
        // agent-conversation is not a direct-enqueue kind (D8) — no audience.
        ChannelMessageKinds.AudienceFor("agent-conversation").Should().BeNull();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static AcceptanceRequest SampleRequest()
    {
        var producer = new DocumentProducer
        {
            Role = "senior_developer",
            Action = "decompose-issue",
            WorkflowDefinitionId = "document-lifecycle",
        };
        var doc = SampleEnvelope("decomposition", producer);
        var review = SampleEnvelope("review", producer);
        return new AcceptanceRequest
        {
            DecisionSessionId = UuidV7.NewGuid(),
            Document = doc,
            Review = review,
            Lineage = new[] { doc },
            RoundsUsed = 1,
            Rules = new ResolvedAcceptanceRules(
                AcceptanceDefaults.Rules, AcceptanceRulesSource.SystemDefault, 1, "decomposition", DateTimeOffset.UtcNow),
            IssueId = "issue-1",
        };
    }

    private static DocumentEnvelope SampleEnvelope(string type, DocumentProducer producer) => new()
    {
        Id = UuidV7.NewGuid(),
        Type = type,
        SchemaVersion = 1,
        IssueId = "issue-1",
        CorrelationId = "corr-1",
        ProducedBy = producer,
        State = DocumentState.Draft,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Payload = JsonDocument.Parse("{}").RootElement.Clone(),
    };
}
