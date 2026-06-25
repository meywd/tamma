using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Debug;

namespace Tamma.Activities.Tests.Debug;

/// <summary>
/// Completeness audit 2026-06-22 (<c>Debugging.md</c> §Missing #8) — coverage for the
/// <c>DEBUG.*</c> DCB event mapping (<see cref="EmitDebugEventActivity.BuildTammaEvent"/>)
/// and the <see cref="DebugEvents"/> status / reason convention.
///
/// <para>The core completeness gap was a real diagnose→fix→verify loop with ZERO audit
/// events. These tests pin the now-explicit contract: diagnosis-failed, tests-failed,
/// invalid-regression and escalated are LOUD (error-status) rows; fix-attempted carries a
/// <c>success</c> flag so a failed-but-continuing fix is visible without being a loud
/// error; tags carry the queryable sessionId/storyId/mode/tenantId/iteration keys.</para>
/// </summary>
[TestFixture]
public class DebugEventTests
{
    [Test]
    public void EventTypes_FollowAggregateActionStatusConvention()
    {
        DebugEvents.SessionStarted.Should().Be("DEBUG.SESSION.STARTED");
        DebugEvents.DiagnosisSuccess.Should().Be("DEBUG.DIAGNOSIS.SUCCESS");
        DebugEvents.DiagnosisFailed.Should().Be("DEBUG.DIAGNOSIS.FAILED");
        DebugEvents.HypothesisSelected.Should().Be("DEBUG.HYPOTHESIS.SELECTED");
        DebugEvents.FixAttempted.Should().Be("DEBUG.FIX.ATTEMPTED");
        DebugEvents.TestsPassed.Should().Be("DEBUG.TESTS.PASSED");
        DebugEvents.TestsFailed.Should().Be("DEBUG.TESTS.FAILED");
        DebugEvents.RegressionInvalid.Should().Be("DEBUG.REGRESSION_TEST.INVALID");
        DebugEvents.ResolvedSuccess.Should().Be("DEBUG.RESOLVED.SUCCESS");
        DebugEvents.Escalated.Should().Be("DEBUG.ESCALATED.FAILED");
    }

    [Test]
    public void StatusForEvent_DegradedTerminalsAreError_RestAreSuccess()
    {
        DebugEvents.StatusForEvent(DebugEvents.DiagnosisFailed).Should().Be("error");
        DebugEvents.StatusForEvent(DebugEvents.TestsFailed).Should().Be("error");
        DebugEvents.StatusForEvent(DebugEvents.RegressionInvalid).Should().Be("error");
        DebugEvents.StatusForEvent(DebugEvents.Escalated).Should().Be("error");

        DebugEvents.StatusForEvent(DebugEvents.SessionStarted).Should().Be("success");
        DebugEvents.StatusForEvent(DebugEvents.DiagnosisSuccess).Should().Be("success");
        DebugEvents.StatusForEvent(DebugEvents.HypothesisSelected).Should().Be("success");
        DebugEvents.StatusForEvent(DebugEvents.FixAttempted).Should().Be("success");
        DebugEvents.StatusForEvent(DebugEvents.TestsPassed).Should().Be("success");
        DebugEvents.StatusForEvent(DebugEvents.ResolvedSuccess).Should().Be("success");
    }

    [Test]
    public void BuildTammaEvent_StampsQueryableTags()
    {
        var tenant = Guid.NewGuid();
        var evt = EmitDebugEventActivity.BuildTammaEvent(
            DebugEvents.HypothesisSelected,
            sessionId: "sess-1", storyId: "7-1I", mode: "BugInvestigation",
            tenantId: tenant, iteration: 2, maxIterations: 5,
            hypothesis: "off-by-one in loop bound", fixSucceeded: false, reason: null);

        evt.EventType.Should().Be(DebugEvents.HypothesisSelected);
        evt.Status.Should().Be("success");
        evt.Tags.Should().NotBeNull();
        evt.Tags!["sessionId"].Should().Be("sess-1");
        evt.Tags["storyId"].Should().Be("7-1I");
        evt.Tags["mode"].Should().Be("BugInvestigation");
        evt.Tags["tenantId"].Should().Be(tenant.ToString("D"));
        evt.Tags["iteration"].Should().Be("2");
        evt.Data["maxIterations"].Should().Be(5);
        evt.Data["hypothesis"].Should().Be("off-by-one in loop bound");
        // fixSucceeded is only present on DEBUG.FIX.ATTEMPTED.
        evt.Data.Should().NotContainKey("fixSucceeded");
    }

    [Test]
    public void BuildTammaEvent_FixAttempted_CarriesSuccessFlag()
    {
        var evtFail = EmitDebugEventActivity.BuildTammaEvent(
            DebugEvents.FixAttempted, "s", "story", "TddFailure", null, 1, 5, "h", fixSucceeded: false, reason: null);
        evtFail.Status.Should().Be("success", "a failed-but-continuing fix is NOT a loud error");
        evtFail.Data["fixSucceeded"].Should().Be(false);

        var evtOk = EmitDebugEventActivity.BuildTammaEvent(
            DebugEvents.FixAttempted, "s", "story", "TddFailure", null, 1, 5, "h", fixSucceeded: true, reason: null);
        evtOk.Data["fixSucceeded"].Should().Be(true);
    }

    [Test]
    public void BuildTammaEvent_NullTenant_OmitsTenantTag()
    {
        var evt = EmitDebugEventActivity.BuildTammaEvent(
            DebugEvents.SessionStarted, "s", "story", "RuntimeError", null, 1, 5, null, false, null);
        evt.Tags!.Should().NotContainKey("tenantId", "single-user / no-tenant events are platform-scope");
    }

    [Test]
    public void BuildTammaEvent_Escalated_CarriesReason_AndIsError()
    {
        var evt = EmitDebugEventActivity.BuildTammaEvent(
            DebugEvents.Escalated, "s", "story", "RuntimeError", null, 6, 5,
            null, false, DebugEvents.ReasonMaxIterations);
        evt.Status.Should().Be("error");
        evt.Data["reason"].Should().Be(DebugEvents.ReasonMaxIterations);
    }

    [Test]
    public void ParseTenantId_RejectsBlankAndGarbage()
    {
        DebugEvents.ParseTenantId(null).Should().BeNull();
        DebugEvents.ParseTenantId("").Should().BeNull();
        DebugEvents.ParseTenantId("not-a-guid").Should().BeNull();
        var g = Guid.NewGuid();
        DebugEvents.ParseTenantId(g.ToString()).Should().Be(g);
    }
}
