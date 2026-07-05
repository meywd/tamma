using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Activities.ADL.Models;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Story 4-6 (Event Capture — Approvals &amp; Escalations) — unit coverage for the
/// plan-approval gate's DCB event catalogue + mapping:
///   - the <c>PLAN_APPROVAL.*</c> type catalogue + status convention (a rejection is a
///     LOUD error row, never a silent approve);
///   - <see cref="PlanApprovalEvents.DecisionEventType"/> maps a resolved decision onto its
///     event type (fail-closed: unknown → REJECTED);
///   - <see cref="WaitForPlanApprovalActivity.BuildTammaEvent"/> maps the gate inputs onto
///     the durable drain event shape with the right tags, status, and decision payload —
///     the same event that is pushed into <c>tamma:events</c> at suspend (REQUESTED) and on
///     resume (DECISION.*).
/// </summary>
[TestFixture]
public class PlanApprovalEventTests
{
    // ================================================================
    // PlanApprovalEvents — type catalogue + status convention
    // ================================================================

    [Test]
    public void EventTypes_FollowAggregateActionStatusConvention()
    {
        PlanApprovalEvents.Requested.Should().Be("PLAN_APPROVAL.REQUESTED");
        PlanApprovalEvents.DecisionApproved.Should().Be("PLAN_APPROVAL.DECISION.APPROVED");
        PlanApprovalEvents.DecisionRejected.Should().Be("PLAN_APPROVAL.DECISION.REJECTED");
        PlanApprovalEvents.DecisionEditRequested.Should().Be("PLAN_APPROVAL.DECISION.EDIT_REQUESTED");
    }

    [Test]
    public void StatusForEvent_RejectionIsError_RestAreSuccess()
    {
        PlanApprovalEvents.StatusForEvent(PlanApprovalEvents.DecisionRejected).Should().Be("error",
            "a rejected plan is a loud audit row, not a false success");
        PlanApprovalEvents.StatusForEvent(PlanApprovalEvents.Requested).Should().Be("success");
        PlanApprovalEvents.StatusForEvent(PlanApprovalEvents.DecisionApproved).Should().Be("success");
        PlanApprovalEvents.StatusForEvent(PlanApprovalEvents.DecisionEditRequested).Should().Be("success");
    }

    [TestCase(ApprovalDecision.Approve, "PLAN_APPROVAL.DECISION.APPROVED")]
    [TestCase(ApprovalDecision.Edit, "PLAN_APPROVAL.DECISION.EDIT_REQUESTED")]
    [TestCase(ApprovalDecision.Reject, "PLAN_APPROVAL.DECISION.REJECTED")]
    [TestCase(ApprovalDecision.Test, "PLAN_APPROVAL.DECISION.REJECTED")] // fail-closed
    public void DecisionEventType_MapsDecisionToType_FailClosed(ApprovalDecision decision, string expected)
    {
        PlanApprovalEvents.DecisionEventType(decision).Should().Be(expected);
    }

    [Test]
    public void ParseTenantId_HandlesEmptyAndValid()
    {
        PlanApprovalEvents.ParseTenantId(null).Should().BeNull();
        PlanApprovalEvents.ParseTenantId("").Should().BeNull();
        PlanApprovalEvents.ParseTenantId("not-a-guid").Should().BeNull();
        var g = Guid.NewGuid();
        PlanApprovalEvents.ParseTenantId(g.ToString()).Should().Be(g);
    }

    // ================================================================
    // WaitForPlanApprovalActivity.BuildTammaEvent — DCB mapping
    // ================================================================

    [Test]
    public void BuildTammaEvent_Requested_AtSuspend_SetsSuccessAndIssueTags_NoDecisionYet()
    {
        // The event pushed into tamma:events at the RAISE point (gate suspend).
        var evt = WaitForPlanApprovalActivity.BuildTammaEvent(
            PlanApprovalEvents.Requested, issueNumber: 21, tenantId: null,
            decision: null, approvedBy: null, feedback: null);

        evt.EventType.Should().Be("PLAN_APPROVAL.REQUESTED");
        evt.Status.Should().Be("success");
        evt.Tags!["issueId"].Should().Be("21");
        evt.Tags["issueNumber"].Should().Be("21");
        evt.Tags.Should().NotContainKey("decision");
        evt.Tags.Should().NotContainKey("approver");
        evt.Tags.Should().NotContainKey("tenantId");
    }

    [Test]
    public void BuildTammaEvent_Approved_AtResume_SetsSuccessDecisionTagsAndData()
    {
        // The event pushed into tamma:events on RESUME (human decision).
        var evt = WaitForPlanApprovalActivity.BuildTammaEvent(
            PlanApprovalEvents.DecisionApproved, issueNumber: 21, tenantId: null,
            decision: "approve", approvedBy: "alice", feedback: "looks good");

        evt.EventType.Should().Be("PLAN_APPROVAL.DECISION.APPROVED");
        evt.Status.Should().Be("success");
        evt.Tags!["decision"].Should().Be("approve");
        evt.Tags["approver"].Should().Be("alice");
        evt.Data["decision"].Should().Be("approve");
        evt.Data["approver"].Should().Be("alice");
        evt.Data["feedback"].Should().Be("looks good");
    }

    [Test]
    public void BuildTammaEvent_Rejected_AtResume_IsErrorStatus()
    {
        var evt = WaitForPlanApprovalActivity.BuildTammaEvent(
            PlanApprovalEvents.DecisionRejected, issueNumber: 7, tenantId: null,
            decision: "reject", approvedBy: "bob", feedback: "wrong approach");

        evt.EventType.Should().Be("PLAN_APPROVAL.DECISION.REJECTED");
        evt.Status.Should().Be("error",
            "a rejected plan is a loud audit row, not a false success");
    }

    [Test]
    public void BuildTammaEvent_WithTenant_SetsTenantIdTag()
    {
        var tenant = Guid.NewGuid();
        var evt = WaitForPlanApprovalActivity.BuildTammaEvent(
            PlanApprovalEvents.DecisionEditRequested, issueNumber: 3, tenantId: tenant,
            decision: "edit", approvedBy: "carol", feedback: "add tests");

        evt.EventType.Should().Be("PLAN_APPROVAL.DECISION.EDIT_REQUESTED");
        evt.Status.Should().Be("success");
        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));
    }

    [Test]
    public void JsonConstructor_DoesNotThrow()
    {
        Action act = () => _ = new WaitForPlanApprovalActivity();
        act.Should().NotThrow();
    }
}
