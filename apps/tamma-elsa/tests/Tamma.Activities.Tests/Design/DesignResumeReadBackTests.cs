using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Design;

namespace Tamma.Activities.Tests.Design;

/// <summary>
/// Story 3.7 — the design workflow's resume callback must read its control-flow boolean
/// (<c>Approved</c>) tolerant of a SERIALIZING workflow runtime (the #15/#437 lesson): the
/// in-process runtime keeps the resumed value a boxed <see cref="bool"/>, but a distributed
/// dispatcher round-trips it to a <see cref="string"/> or a <see cref="JsonElement"/>. A bare
/// <c>is true</c> pattern only matches the boxed-bool path — under serialization a rejection
/// would silently be read as an approval, returning HTTP 200 while advancing the WRONG branch.
/// These tests also cover the fail-closed <see cref="DesignParsing"/> helper, the canonical
/// bookmark-name builder (suspend/resume parity + tenant folding), and the DESIGN.* event
/// status mapping.
/// </summary>
[TestFixture]
public class DesignResumeReadBackTests
{
    private static JsonElement JsonBool(bool value) => JsonDocument.Parse(value ? "true" : "false").RootElement;

    private static IDictionary<string, object> Input(params (string Key, object Value)[] entries)
    {
        var dict = new Dictionary<string, object>();
        foreach (var (key, value) in entries)
            dict[key] = value;
        return dict;
    }

    // ── ReadDecision — Approved coercion tolerant of serialization ─────────

    [Test]
    public void ReadDecision_BoxedBoolTrue_ReachesApproved()
    {
        var (approved, feedback) = WaitForDesignApprovalActivity.ReadDecision(
            Input(("Approved", true), ("Feedback", "ship it")));
        approved.Should().BeTrue();
        feedback.Should().Be("ship it");
    }

    [Test]
    public void ReadDecision_StringTrue_ReachesApproved()
    {
        WaitForDesignApprovalActivity.ReadDecision(Input(("Approved", "true"))).Approved.Should().BeTrue();
        WaitForDesignApprovalActivity.ReadDecision(Input(("Approved", "True"))).Approved.Should().BeTrue();
    }

    [Test]
    public void ReadDecision_JsonElementTrue_ReachesApproved()
    {
        WaitForDesignApprovalActivity.ReadDecision(Input(("Approved", JsonBool(true)))).Approved.Should().BeTrue();
    }

    [Test]
    public void ReadDecision_FalseRepresentations_ReachRejected()
    {
        WaitForDesignApprovalActivity.ReadDecision(Input(("Approved", false))).Approved.Should().BeFalse();
        WaitForDesignApprovalActivity.ReadDecision(Input(("Approved", "false"))).Approved.Should().BeFalse();
        WaitForDesignApprovalActivity.ReadDecision(Input(("Approved", JsonBool(false)))).Approved.Should().BeFalse();
    }

    [Test]
    public void ReadDecision_MissingKey_ReachesRejected_FailClosed()
    {
        var (approved, feedback) = WaitForDesignApprovalActivity.ReadDecision(Input(("Feedback", "n/a")));
        approved.Should().BeFalse("a missing Approved flag must fail closed to a rejection, never a false approval");
        feedback.Should().Be("n/a");
    }

    // ── Canonical bookmark name — suspend/resume parity + tenant folding ───

    [Test]
    public void ApprovalBookmarkName_IsDeterministic_AndFoldsTenant()
    {
        var session = Guid.NewGuid();
        var tenantA = Guid.NewGuid().ToString();
        var tenantB = Guid.NewGuid().ToString();

        var a1 = WaitForDesignApprovalActivity.ApprovalBookmarkName(tenantA, session);
        var a2 = WaitForDesignApprovalActivity.ApprovalBookmarkName(tenantA, session);
        var b1 = WaitForDesignApprovalActivity.ApprovalBookmarkName(tenantB, session);

        a1.Should().Be(a2, "the builder must be deterministic so suspend + resume names match byte-for-byte");
        a1.Should().StartWith("design-approval-");
        a1.Should().Contain(session.ToString());
        a1.Should().NotBe(b1,
            "folding the tenant into the name is the IDOR guard — a different tenant yields a " +
            "different bookmark so a cross-tenant resume can never resolve this gate");
    }

    [Test]
    public void ApprovalBookmarkName_NullTenant_UsesStablePlaceholder()
    {
        var session = Guid.NewGuid();
        WaitForDesignApprovalActivity.ApprovalBookmarkName(null, session)
            .Should().Be($"design-approval-none-{session}");
    }

    // ── DesignParsing.ParseProposal — tolerant + fail-closed ───────────────

    [Test]
    public void ParseProposal_FullObject_Parses()
    {
        var proposal = DesignParsing.ParseProposal(
            "Here you go: {\"summary\":\"Event-sourced ledger\"," +
            "\"alternatives\":[{\"name\":\"CQRS\",\"tradeoffs\":\"more moving parts\"}," +
            "{\"name\":\"CRUD\",\"tradeoffs\":\"simpler, weaker audit\"}]," +
            "\"recommendation\":\"CQRS\",\"constraintEvaluation\":\"meets SOC2\"} done");

        proposal.Should().NotBeNull();
        proposal!.Summary.Should().Be("Event-sourced ledger");
        proposal.Alternatives.Should().HaveCount(2);
        proposal.Alternatives[0].Name.Should().Be("CQRS");
        proposal.Recommendation.Should().Be("CQRS");
        proposal.ConstraintEvaluation.Should().Be("meets SOC2");
    }

    [Test]
    public void ParseProposal_StringAlternatives_Parses()
    {
        var proposal = DesignParsing.ParseProposal(
            "{\"summary\":\"S\",\"alternatives\":[\"Option A\",\"Option B\"]}");
        proposal.Should().NotBeNull();
        proposal!.Alternatives.Select(a => a.Name).Should().Equal("Option A", "Option B");
    }

    [Test]
    public void ParseProposal_MissingSummary_IsNull_FailClosed()
    {
        DesignParsing.ParseProposal("{\"recommendation\":\"do X\"}").Should().BeNull();
        DesignParsing.ParseProposal("{\"summary\":\"\"}").Should().BeNull();
        DesignParsing.ParseProposal("not json").Should().BeNull();
        DesignParsing.ParseProposal("").Should().BeNull();
        DesignParsing.ParseProposal(null).Should().BeNull();
    }

    // ── DesignEvents.StatusForEvent — LOUD terminals are error rows ────────

    [Test]
    public void StatusForEvent_FailedAndTimedOut_AreErrorRows()
    {
        DesignEvents.StatusForEvent(DesignEvents.ProposalFailed).Should().Be("error");
        DesignEvents.StatusForEvent(DesignEvents.ReviewTimedOut).Should().Be("error");
    }

    [Test]
    public void StatusForEvent_NormalTransitions_AreSuccessRows()
    {
        DesignEvents.StatusForEvent(DesignEvents.ProposalGenerated).Should().Be("success");
        DesignEvents.StatusForEvent(DesignEvents.ProposalDelivered).Should().Be("success");
        DesignEvents.StatusForEvent(DesignEvents.ProposalApproved).Should().Be("success");
        DesignEvents.StatusForEvent(DesignEvents.ProposalRejected).Should().Be("success",
            "a rejection is a legitimate human decision, not an error");
    }

    // ── EmitDesignEventActivity.BuildTammaEvent — tag/data mapping ─────────

    [Test]
    public void BuildTammaEvent_MapsTagsAndStatus()
    {
        var tenant = Guid.NewGuid();
        var evt = EmitDesignEventActivity.BuildTammaEvent(
            DesignEvents.ProposalApproved, "sess-1", "issue-9", tenant, channel: null,
            alternativeCount: 3, detail: "approved with nits", reviewer: "alice@x.test");

        evt.EventType.Should().Be(DesignEvents.ProposalApproved);
        evt.Status.Should().Be("success");
        evt.Tags!["sessionId"].Should().Be("sess-1");
        evt.Tags!["issueId"].Should().Be("issue-9");
        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));
        evt.Data["alternativeCount"].Should().Be(3);
        evt.Data["reviewer"].Should().Be("alice@x.test");
    }

    [Test]
    public void BuildTammaEvent_FailedIsLoudErrorRow()
    {
        var evt = EmitDesignEventActivity.BuildTammaEvent(
            DesignEvents.ProposalFailed, "sess-1", "issue-9", tenantId: null, channel: null,
            alternativeCount: 0, detail: "llm-call failed", reviewer: null);

        evt.Status.Should().Be("error", "a generation failure must be a LOUD error row, never a false success");
    }
}
