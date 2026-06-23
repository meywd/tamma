using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Completeness audit 2026-06-22 (<c>TddWithDebugRetry.md</c>) — coverage for the
/// <c>TDD_DEBUG.*</c> DCB event mapping
/// (<see cref="EmitTddDebugEventActivity.BuildTammaEvent"/>) and the
/// <see cref="TddDebugEvents"/> status / finish-reason convention.
///
/// <para>The core completeness gap was a thin orchestrator with no audit events and a
/// generic "retry limit reached" failure string that dropped the real cause. These
/// tests pin the now-explicit contract: a retry-exhausted loop and a debugger
/// escalation are LOUD (error-status) audit rows carrying a <c>finishReason</c> and
/// the underlying <c>errorDetail</c> — never a silent false success.</para>
/// </summary>
[TestFixture]
public class TddDebugEventTests
{
    // ================================================================
    // TddDebugEvents — type catalogue + status convention (no false success)
    // ================================================================

    [Test]
    public void EventTypes_FollowAggregateActionStatusConvention()
    {
        TddDebugEvents.CycleStarted.Should().Be("TDD_DEBUG.CYCLE.STARTED");
        TddDebugEvents.CyclePassed.Should().Be("TDD_DEBUG.CYCLE.PASSED");
        TddDebugEvents.CycleFailed.Should().Be("TDD_DEBUG.CYCLE.FAILED");
        TddDebugEvents.DebugAttempted.Should().Be("TDD_DEBUG.DEBUG.ATTEMPTED");
        TddDebugEvents.DebuggerEscalated.Should().Be("TDD_DEBUG.DEBUGGER.ESCALATED");
        TddDebugEvents.RetryExhausted.Should().Be("TDD_DEBUG.RETRY.EXHAUSTED");
        TddDebugEvents.CompletedSuccess.Should().Be("TDD_DEBUG.COMPLETED.SUCCESS");
    }

    [Test]
    public void StatusForEvent_ExhaustionAndEscalationAreError_RestAreSuccess()
    {
        TddDebugEvents.StatusForEvent(TddDebugEvents.RetryExhausted).Should().Be("error");
        TddDebugEvents.StatusForEvent(TddDebugEvents.DebuggerEscalated).Should().Be("error");

        TddDebugEvents.StatusForEvent(TddDebugEvents.CycleStarted).Should().Be("success");
        TddDebugEvents.StatusForEvent(TddDebugEvents.CyclePassed).Should().Be("success");
        // A FAILED cycle is an expected, recoverable loop transition (it gets retried),
        // so it is NOT an error-status row — only the terminal exhaustion is.
        TddDebugEvents.StatusForEvent(TddDebugEvents.CycleFailed).Should().Be("success");
        TddDebugEvents.StatusForEvent(TddDebugEvents.DebugAttempted).Should().Be("success");
        TddDebugEvents.StatusForEvent(TddDebugEvents.CompletedSuccess).Should().Be("success");
    }

    [Test]
    public void FinishReasons_DistinguishNonConvergenceFromDebuggerCrash()
    {
        TddDebugEvents.ReasonNotConverged.Should().Be("tdd-not-converged");
        TddDebugEvents.ReasonDebuggerEscalated.Should().Be("debugger-escalated");
        TddDebugEvents.ReasonNotConverged.Should().NotBe(TddDebugEvents.ReasonDebuggerEscalated);
    }

    [Test]
    public void ParseTenantId_ValidGuid_Parses_InvalidOrEmpty_IsNull()
    {
        var g = Guid.NewGuid();
        TddDebugEvents.ParseTenantId(g.ToString()).Should().Be(g);
        TddDebugEvents.ParseTenantId("").Should().BeNull();
        TddDebugEvents.ParseTenantId(null).Should().BeNull();
        TddDebugEvents.ParseTenantId("not-a-guid").Should().BeNull();
    }

    // ================================================================
    // BuildTammaEvent — tags + data + status mapping
    // ================================================================

    [Test]
    public void BuildTammaEvent_CompletedSuccess_HasSuccessStatus_AndLoopPayload()
    {
        var evt = EmitTddDebugEventActivity.BuildTammaEvent(
            TddDebugEvents.CompletedSuccess,
            storyId: "story-7",
            issueNumber: 7,
            repository: "owner/repo",
            tenantId: null,
            attempt: 2,
            maxRetries: 3,
            finishReason: null,
            errorDetail: null);

        evt.EventType.Should().Be("TDD_DEBUG.COMPLETED.SUCCESS");
        evt.Status.Should().Be("success");

        evt.Tags!["storyId"].Should().Be("story-7");
        evt.Tags!["issueId"].Should().Be("7");
        evt.Tags!["issueNumber"].Should().Be("7");
        evt.Tags!["repository"].Should().Be("owner/repo");
        evt.Tags!["attempt"].Should().Be("2");
        evt.Tags.Should().NotContainKey("tenantId", "single-user / platform-scope event");

        evt.Data["attempt"].Should().Be(2);
        evt.Data["maxRetries"].Should().Be(3);
        evt.Data.Should().NotContainKey("finishReason");
        evt.Data.Should().NotContainKey("errorDetail");
    }

    [Test]
    public void BuildTammaEvent_RetryExhausted_HasErrorStatus_AndSurfacesRealCause()
    {
        // The core no-false-success guarantee: an exhausted loop is a LOUD (error)
        // audit row that carries the REAL underlying failure, not a generic string.
        var evt = EmitTddDebugEventActivity.BuildTammaEvent(
            TddDebugEvents.RetryExhausted,
            storyId: "story-9",
            issueNumber: 9,
            repository: "owner/repo",
            tenantId: null,
            attempt: 3,
            maxRetries: 3,
            finishReason: TddDebugEvents.ReasonNotConverged,
            errorDetail: "GREEN phase failed: 2 tests still red in auth.spec.ts");

        evt.EventType.Should().Be("TDD_DEBUG.RETRY.EXHAUSTED");
        evt.Status.Should().Be("error");
        evt.Data["finishReason"].Should().Be("tdd-not-converged");
        evt.Data["errorDetail"].Should().Be("GREEN phase failed: 2 tests still red in auth.spec.ts");
    }

    [Test]
    public void BuildTammaEvent_DebuggerEscalated_HasErrorStatus_AndReason()
    {
        var evt = EmitTddDebugEventActivity.BuildTammaEvent(
            TddDebugEvents.DebuggerEscalated,
            storyId: "story-9",
            issueNumber: 9,
            repository: "owner/repo",
            tenantId: null,
            attempt: 1,
            maxRetries: 3,
            finishReason: TddDebugEvents.ReasonDebuggerEscalated,
            errorDetail: "debugger could not produce a fix");

        evt.Status.Should().Be("error");
        evt.Data["finishReason"].Should().Be("debugger-escalated");
    }

    [Test]
    public void BuildTammaEvent_WithTenant_StampsTenantTag()
    {
        var tenant = Guid.NewGuid();
        var evt = EmitTddDebugEventActivity.BuildTammaEvent(
            TddDebugEvents.CycleStarted, "story-1", 1, "owner/repo", tenant, 0, 3, null, null);

        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));
    }

    [Test]
    public void BuildTammaEvent_ZeroIssueNumber_OmitsIssueTags_KeepsAttempt()
    {
        var evt = EmitTddDebugEventActivity.BuildTammaEvent(
            TddDebugEvents.CycleStarted, "story-1", 0, "owner/repo", null, 1, 3, null, null);

        evt.Tags!.Should().NotContainKey("issueId");
        evt.Tags!.Should().NotContainKey("issueNumber");
        evt.Tags!["attempt"].Should().Be("1");
        evt.Tags!["storyId"].Should().Be("story-1");
    }
}
