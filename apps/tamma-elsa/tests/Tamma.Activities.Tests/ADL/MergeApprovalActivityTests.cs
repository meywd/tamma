using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Unit coverage for the merge-approval gate's pure decision logic and DCB event
/// mapping (FR-19 / FR-34 / Story 4-6):
///   - decision normalisation: merge/test/reject map to typed outcomes;
///     unknown/empty → <c>Invalid</c> (NEVER a silent "reject");
///   - <c>EmitMergeApprovalEventActivity.BuildTammaEvent</c> maps onto the durable
///     drain event shape with the right tags, status and decision payload;
///   - reject/invalid/escalated are loud (error-status) audit rows, not false success.
/// </summary>
[TestFixture]
public class MergeApprovalActivityTests
{
    // ================================================================
    // WaitForMergeApprovalActivity.Normalize — no silent reject
    // ================================================================

    [TestCase("merge", "Merge", "merge")]
    [TestCase("MERGE", "Merge", "merge")]
    [TestCase("  Merge  ", "Merge", "merge")]
    [TestCase("test", "Test", "test")]
    [TestCase("Test", "Test", "test")]
    [TestCase("reject", "Reject", "reject")]
    [TestCase("REJECT", "Reject", "reject")]
    public void Normalize_KnownDecisions_MapToTypedOutcomes(string input, string outcome, string token)
    {
        var (o, n) = WaitForMergeApprovalActivity.Normalize(input);
        o.Should().Be(outcome);
        n.Should().Be(token);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("approve")]      // close-but-wrong word
    [TestCase("merg")]         // typo
    [TestCase("yes")]
    public void Normalize_UnknownOrEmpty_IsInvalid_NotSilentReject(string? input)
    {
        var (o, n) = WaitForMergeApprovalActivity.Normalize(input);
        o.Should().Be("Invalid",
            "an unknown/empty decision must be an explicit Invalid outcome — never a silent reject");
        n.Should().Be("invalid");
        o.Should().NotBe("Reject");
    }

    // ================================================================
    // EmitMergeApprovalEventActivity.BuildTammaEvent — DCB mapping
    // ================================================================

    [Test]
    public void BuildTammaEvent_MergeRequested_SetsSuccessStatusTagsAndData()
    {
        var evt = EmitMergeApprovalEventActivity.BuildTammaEvent(
            MergeApprovalEvents.MergeRequested, issueNumber: 12, prNumber: 34,
            tenantId: null, decision: "merge", approver: "alice", feedback: "lgtm");

        evt.EventType.Should().Be("MERGE.REQUESTED");
        evt.Status.Should().Be("success");
        evt.Tags.Should().NotBeNull();
        evt.Tags!["issueId"].Should().Be("12");
        evt.Tags["issueNumber"].Should().Be("12");
        evt.Tags["prNumber"].Should().Be("34");
        evt.Tags["decision"].Should().Be("merge");
        evt.Tags["approver"].Should().Be("alice");
        evt.Tags.Should().NotContainKey("tenantId");
        evt.Data["decision"].Should().Be("merge");
        evt.Data["approver"].Should().Be("alice");
        evt.Data["feedback"].Should().Be("lgtm");
    }

    [Test]
    public void BuildTammaEvent_Rejected_SetsErrorStatus()
    {
        var evt = EmitMergeApprovalEventActivity.BuildTammaEvent(
            MergeApprovalEvents.DecisionRejected, issueNumber: 7, prNumber: 8,
            tenantId: null, decision: "reject", approver: "bob", feedback: "needs work");

        evt.EventType.Should().Be("MERGE_APPROVAL.DECISION.REJECTED");
        evt.Status.Should().Be("error",
            "a rejection is a loud audit row, not a false success");
    }

    [Test]
    public void BuildTammaEvent_Escalated_SetsErrorStatus()
    {
        var evt = EmitMergeApprovalEventActivity.BuildTammaEvent(
            MergeApprovalEvents.Escalated, 1, 2, null, "invalid", null, null);

        evt.Status.Should().Be("error");
    }

    [Test]
    public void BuildTammaEvent_OmitsZeroPrNumber_And_EmptyOptionalTags()
    {
        var evt = EmitMergeApprovalEventActivity.BuildTammaEvent(
            MergeApprovalEvents.DecisionInvalid, issueNumber: 5, prNumber: 0,
            tenantId: null, decision: null, approver: null, feedback: null);

        evt.Tags!.Should().NotContainKey("prNumber");
        evt.Tags.Should().NotContainKey("decision");
        evt.Tags.Should().NotContainKey("approver");
    }

    [Test]
    public void BuildTammaEvent_WithTenant_SetsTenantIdTag()
    {
        var tenant = Guid.NewGuid();
        var evt = EmitMergeApprovalEventActivity.BuildTammaEvent(
            MergeApprovalEvents.MergeRequested, 1, 2, tenant, "merge", "a", "");

        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));
    }

    [Test]
    public void IsFailureEvent_RejectInvalidEscalated_AreFailures_MergeTestAreNot()
    {
        EmitMergeApprovalEventActivity.IsFailureEvent(MergeApprovalEvents.DecisionRejected).Should().BeTrue();
        EmitMergeApprovalEventActivity.IsFailureEvent(MergeApprovalEvents.DecisionInvalid).Should().BeTrue();
        EmitMergeApprovalEventActivity.IsFailureEvent(MergeApprovalEvents.Escalated).Should().BeTrue();

        EmitMergeApprovalEventActivity.IsFailureEvent(MergeApprovalEvents.MergeRequested).Should().BeFalse();
        EmitMergeApprovalEventActivity.IsFailureEvent(MergeApprovalEvents.TestRequested).Should().BeFalse();
        EmitMergeApprovalEventActivity.IsFailureEvent(MergeApprovalEvents.DecisionMerged).Should().BeFalse();
        EmitMergeApprovalEventActivity.IsFailureEvent(MergeApprovalEvents.DecisionTest).Should().BeFalse();
    }

    [Test]
    public void ParseTenantId_HandlesEmptyAndValid()
    {
        MergeApprovalEvents.ParseTenantId(null).Should().BeNull();
        MergeApprovalEvents.ParseTenantId("").Should().BeNull();
        MergeApprovalEvents.ParseTenantId("not-a-guid").Should().BeNull();
        var g = Guid.NewGuid();
        MergeApprovalEvents.ParseTenantId(g.ToString()).Should().Be(g);
    }

    // ================================================================
    // SECURITY C2 — BookmarkName uniqueness / determinism
    // ================================================================

    [Test]
    public void BookmarkName_IncludesTenantAndRepo_NoCrossTenantCollision()
    {
        var tA = Guid.NewGuid().ToString();
        var tB = Guid.NewGuid().ToString();

        // Same issue/PR, different tenant → DISTINCT names (the C2 collision the
        // old `adl-merge-approval-{issue}-{pr}` name had).
        WaitForMergeApprovalActivity.BookmarkName(tA, "octo/repo", 5, 5)
            .Should().NotBe(WaitForMergeApprovalActivity.BookmarkName(tB, "octo/repo", 5, 5));

        // Same issue/PR + tenant, different repo → DISTINCT names too.
        WaitForMergeApprovalActivity.BookmarkName(tA, "octo/repo", 5, 5)
            .Should().NotBe(WaitForMergeApprovalActivity.BookmarkName(tA, "octo/other", 5, 5));
    }

    [Test]
    public void BookmarkName_IsDeterministic_SameInputsSameName()
    {
        var t = Guid.NewGuid().ToString();
        WaitForMergeApprovalActivity.BookmarkName(t, "Octo/Repo", 5, 7)
            .Should().Be(WaitForMergeApprovalActivity.BookmarkName(t, "Octo/Repo", 5, 7),
                "suspend-side and resume-side must compute identical names");
    }

    [Test]
    public void BookmarkName_NormalizesSeparators_SoSegmentsCannotForgeAName()
    {
        // A repo whose chars include the '-' delimiter must not be able to alias a
        // different (tenant, repo, issue, pr) tuple. Normalisation maps '-' and '/'
        // to '_', so a crafted segment can't smuggle in extra delimiters.
        var t = Guid.NewGuid().ToString();
        var crafted = WaitForMergeApprovalActivity.BookmarkName(t, "a-b-9-9", 5, 5);
        crafted.Should().Contain("a_b_9_9");
        crafted.Should().EndWith("-5-5");
    }

    [TestCase(null, "none")]
    [TestCase("", "none")]
    [TestCase("   ", "none")]
    [TestCase("Octo/Repo", "octo_repo")]
    [TestCase("a-b", "a_b")]
    public void NormalizeSegment_LowersAndReplacesDelimiters(string? input, string expected)
    {
        WaitForMergeApprovalActivity.NormalizeSegment(input).Should().Be(expected);
    }
}
