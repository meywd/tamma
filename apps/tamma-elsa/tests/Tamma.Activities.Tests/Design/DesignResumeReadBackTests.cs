using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Design;
using Tamma.Activities.Documents;
using Tamma.ElsaServer.Endpoints;

namespace Tamma.Activities.Tests.Design;

/// <summary>
/// Story 39-13 (D4) — retargeted from the legacy <c>WaitForDesignApprovalActivity</c>: design
/// acceptance now rides 39-8's generic decision gate, and <c>DesignResumeEndpoint</c> is a thin
/// adapter that TRANSLATES the legacy approve/reject payload into an
/// <c>AcceptanceDecision</c> and forwards to the generic decision-resume path. The
/// serialization-tolerance of the boolean is now the generic gate's concern
/// (<c>DocumentDecisionResumeEndpointTests</c>); here we pin the adapter's decision mapping and
/// its bookmark parity with the canonical decision-session name. The <c>DesignParsing</c> rows
/// retired with the parser (39-13 D9). The DESIGN.* event mapping tests are unchanged.
/// </summary>
[TestFixture]
public class DesignResumeReadBackTests
{
    private static DesignResumeEndpoint.ResumeRequest Req(bool approved, string? feedback) =>
        new(Guid.NewGuid(), Guid.NewGuid().ToString(), approved, feedback, Reviewer: "who@x.test");

    // ── Adapter decision mapping — approve → accept, reject → reject(reason) ──

    [Test]
    public void ToDecisionJson_Approved_MapsToAccept()
    {
        var json = DesignResumeEndpoint.ToDecisionJson(Req(approved: true, feedback: "ship it"));
        json.Should().Contain("\"kind\":\"accept\"", "an approval maps to AcceptanceDecision.Accept");
        json.Should().NotContain("reject");
    }

    [Test]
    public void ToDecisionJson_Rejected_MapsToRejectWithFeedbackReason()
    {
        var json = DesignResumeEndpoint.ToDecisionJson(Req(approved: false, feedback: "revise the data model"));
        json.Should().Contain("\"kind\":\"reject\"", "a rejection maps to AcceptanceDecision.Reject");
        json.Should().Contain("revise the data model", "the feedback carries onto the reject reason");
    }

    [Test]
    public void ToDecisionJson_RejectedNoFeedback_UsesEmptyReason()
    {
        var json = DesignResumeEndpoint.ToDecisionJson(Req(approved: false, feedback: null));
        json.Should().Contain("\"kind\":\"reject\"");
    }

    // ── Bookmark parity — the adapter resolves the canonical decision gate ─

    [Test]
    public void Adapter_BookmarkName_MatchesTheDecisionSessionGate_AndFoldsTenant()
    {
        var session = Guid.NewGuid();
        var tenantA = Guid.NewGuid().ToString();
        var tenantB = Guid.NewGuid().ToString();

        var adapterName = DesignResumeEndpoint.BookmarkName(new(session, tenantA, true, "fb", "rev"));
        var gateName = LifecycleBookmarks.ForDecisionSession(tenantA, session);

        adapterName.Should().Be(gateName, "the adapter must compute the SAME name the lifecycle accept-gate suspends on");
        adapterName.Should().StartWith("document-decision-");
        adapterName.Should().Contain(session.ToString());
        adapterName.Should().NotBe(LifecycleBookmarks.ForDecisionSession(tenantB, session),
            "folding the tenant is the IDOR guard — a different tenant yields a different bookmark");
    }

    [Test]
    public void BookmarkName_NullTenant_UsesStablePlaceholder()
    {
        var session = Guid.NewGuid();
        LifecycleBookmarks.ForDecisionSession(null, session)
            .Should().Be($"document-decision-none-{session}");
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
