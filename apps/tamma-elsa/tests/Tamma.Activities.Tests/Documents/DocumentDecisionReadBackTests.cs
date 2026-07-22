using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Activities.Tests.Documents;

/// <summary>
/// Story 39-8 (AC4). The generic gate's resume callback must read the injected decision
/// tolerant of a SERIALIZING workflow runtime (the #15/#437 lesson), map each 39-5
/// <c>kind</c> onto the right flowchart outcome, and FAIL-CLOSE an unparseable
/// <c>DecisionJson</c> to <c>Escalate(AcceptorJudgment)</c> — never a mis-branch that returns
/// 200 while advancing the wrong edge. Also covers the canonical bookmark-name builder
/// (suspend/resume parity + tenant folding + hostile-segment normalization).
/// </summary>
[TestFixture]
public class DocumentDecisionReadBackTests
{
    private static IDictionary<string, object> Input(params (string Key, object Value)[] entries)
    {
        var dict = new Dictionary<string, object>();
        foreach (var (key, value) in entries)
            dict[key] = value;
        return dict;
    }

    private static JsonElement JsonStr(string s) => JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement;

    // ── kind → outcome mapping ─────────────────────────────────────────────

    [Test]
    public void ReadDecision_Accept_MapsToAcceptOutcome()
    {
        var read = WaitForDocumentDecisionActivity.ReadDecision(
            Input(("DecisionJson", "{\"kind\":\"accept\"}")));
        read.Outcome.Should().Be("Accept");
        read.DecisionKind.Should().Be("accept");
        read.Decision.Should().BeOfType<AcceptanceDecision.Accept>();
    }

    [Test]
    public void ReadDecision_RequestRevision_MapsToRequestRevisionOutcome()
    {
        var read = WaitForDocumentDecisionActivity.ReadDecision(
            Input(("DecisionJson", "{\"kind\":\"request-revision\",\"notes\":\"tighten the data model\"}")));
        read.Outcome.Should().Be("RequestRevision");
        read.DecisionKind.Should().Be("request-revision");
        read.Decision.Should().BeOfType<AcceptanceDecision.RequestRevision>()
            .Which.Notes.Should().Be("tighten the data model");
    }

    [Test]
    public void ReadDecision_Reject_MapsToRejectOutcome()
    {
        var read = WaitForDocumentDecisionActivity.ReadDecision(
            Input(("DecisionJson", "{\"kind\":\"reject\",\"reason\":\"wrong approach\"}")));
        read.Outcome.Should().Be("Reject");
        read.DecisionKind.Should().Be("reject");
        read.Decision.Should().BeOfType<AcceptanceDecision.Reject>()
            .Which.Reason.Should().Be("wrong approach");
    }

    [Test]
    public void ReadDecision_Escalate_MapsToEscalateOutcome()
    {
        var read = WaitForDocumentDecisionActivity.ReadDecision(
            Input(("DecisionJson", "{\"kind\":\"escalate\",\"reason\":\"acceptor-judgment\",\"detail\":\"unsure\"}")));
        read.Outcome.Should().Be("Escalate");
        read.DecisionKind.Should().Be("escalate");
        read.Decision.Should().BeOfType<AcceptanceDecision.Escalate>()
            .Which.Reason.Should().Be(AcceptanceEscalationReason.AcceptorJudgment);
    }

    // ── fail-closed on garbage (D5) ────────────────────────────────────────

    [Test]
    public void ReadDecision_UnparseableJson_FailClosesToEscalate()
    {
        var read = WaitForDocumentDecisionActivity.ReadDecision(
            Input(("DecisionJson", "not json at all")));
        read.Outcome.Should().Be("Escalate",
            "an unreadable decision payload must fail closed to Escalate, never mis-branch while returning 200");
        read.Decision.Should().BeOfType<AcceptanceDecision.Escalate>()
            .Which.Reason.Should().Be(AcceptanceEscalationReason.AcceptorJudgment);
    }

    [Test]
    public void ReadDecision_MissingDecisionJson_FailClosesToEscalate()
    {
        var read = WaitForDocumentDecisionActivity.ReadDecision(
            Input(("Feedback", "n/a")));
        read.Outcome.Should().Be("Escalate",
            "a missing decision must fail closed to Escalate, never a false accept");
    }

    // ── serialization tolerance of the scalar fields ───────────────────────

    [Test]
    public void ReadDecision_ReadsScalarFields_TolerantOfStringAndJsonElement()
    {
        var read = WaitForDocumentDecisionActivity.ReadDecision(Input(
            ("DecisionJson", "{\"kind\":\"accept\"}"),
            ("Feedback", "looks good"),
            ("DeciderId", "alice@x.test"),
            ("DeciderDisplay", JsonStr("Alice")),
            ("Channel", "user"),
            ("RulesReference", "type-override@2")));

        read.Feedback.Should().Be("looks good");
        read.DeciderId.Should().Be("alice@x.test");
        read.DeciderDisplay.Should().Be("Alice");
        read.Channel.Should().Be("user");
        read.RulesReference.Should().Be("type-override@2");
    }

    [Test]
    public void ReadDecision_MissingChannel_DefaultsToOrchestrator()
    {
        var read = WaitForDocumentDecisionActivity.ReadDecision(
            Input(("DecisionJson", "{\"kind\":\"accept\"}")));
        read.Channel.Should().Be("orchestrator",
            "a decision with no explicit channel is treated as the orchestrator self-decision");
    }

    [Test]
    public void ReadDecision_CanonicalizesDecisionJson()
    {
        // The output DecisionJson must be a valid re-serialized AcceptanceDecision the 39-6
        // guardrail can deserialize, even when the input had extra whitespace / ordering.
        var read = WaitForDocumentDecisionActivity.ReadDecision(
            Input(("DecisionJson", "  {  \"kind\" : \"accept\"  }  ")));
        var reparsed = JsonSerializer.Deserialize<AcceptanceDecision>(read.DecisionJson);
        reparsed.Should().BeOfType<AcceptanceDecision.Accept>();
    }

    // ── canonical bookmark name — parity + tenant folding + normalization ──

    [Test]
    public void DecisionBookmarkName_IsDeterministic_AndFoldsTenant()
    {
        var session = Guid.NewGuid();
        var tenantA = Guid.NewGuid().ToString();
        var tenantB = Guid.NewGuid().ToString();

        var a1 = WaitForDocumentDecisionActivity.DecisionBookmarkName(tenantA, session);
        var a2 = WaitForDocumentDecisionActivity.DecisionBookmarkName(tenantA, session);
        var b1 = WaitForDocumentDecisionActivity.DecisionBookmarkName(tenantB, session);

        a1.Should().Be(a2, "the builder must be deterministic so suspend + resume names match byte-for-byte");
        a1.Should().StartWith("document-decision-");
        a1.Should().Contain(session.ToString());
        a1.Should().NotBe(b1, "folding the tenant into the name is the IDOR guard");
    }

    [Test]
    public void DecisionBookmarkName_NullTenant_UsesStablePlaceholder()
    {
        var session = Guid.NewGuid();
        WaitForDocumentDecisionActivity.DecisionBookmarkName(null, session)
            .Should().Be($"document-decision-none-{session}");
    }

    [Test]
    public void DecisionBookmarkName_HostileSegment_IsNormalized()
    {
        var session = Guid.NewGuid();
        // A hostile tenant segment can't smuggle delimiters into the name.
        WaitForDocumentDecisionActivity.DecisionBookmarkName("Ten ANT/../x-9", session)
            .Should().Be($"document-decision-ten_ant_.._x_9-{session}");
    }
}
