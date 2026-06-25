using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Completeness audit 2026-06-22 (<c>SingleIssueCycle.md</c> §Missing #1) — coverage for
/// the cycle-scoped <c>CYCLE.*</c> DCB event mapping
/// (<see cref="EmitCycleEventActivity.BuildTammaEvent"/>) and the <see cref="CycleEvents"/>
/// status convention. These pin the no-false-success contract: a step failure and a cycle
/// failure are LOUD (error-status) audit rows carrying the failing <c>stepId</c> and the
/// underlying detail — never a silently swallowed COMPLETED.
/// </summary>
[TestFixture]
public class CycleEventTests
{
    [Test]
    public void EventTypes_FollowAggregateActionStatusConvention()
    {
        CycleEvents.Started.Should().Be("CYCLE.STARTED");
        CycleEvents.StepFailed.Should().Be("CYCLE.STEP_FAILED");
        CycleEvents.Completed.Should().Be("CYCLE.COMPLETED");
        CycleEvents.Failed.Should().Be("CYCLE.FAILED");
    }

    [Test]
    public void StatusForEvent_StepFailedAndFailedAreError_RestAreSuccess()
    {
        CycleEvents.StatusForEvent(CycleEvents.StepFailed).Should().Be("error");
        CycleEvents.StatusForEvent(CycleEvents.Failed).Should().Be("error");
        CycleEvents.StatusForEvent(CycleEvents.Started).Should().Be("success");
        CycleEvents.StatusForEvent(CycleEvents.Completed).Should().Be("success");
    }

    [Test]
    public void ParseTenantId_EmptyOrUnparseable_IsNull_RealGuidParses()
    {
        CycleEvents.ParseTenantId(null).Should().BeNull();
        CycleEvents.ParseTenantId("").Should().BeNull();
        CycleEvents.ParseTenantId("not-a-guid").Should().BeNull();

        var g = Guid.NewGuid();
        CycleEvents.ParseTenantId(g.ToString()).Should().Be(g);
    }

    [Test]
    public void BuildTammaEvent_StepFailed_CarriesStepId_AndIsErrorStatus()
    {
        var tenant = Guid.NewGuid();
        var evt = EmitCycleEventActivity.BuildTammaEvent(
            CycleEvents.StepFailed,
            issueNumber: 42,
            repository: "owner/repo",
            tenantId: tenant,
            stepId: "plan-generation",
            errorDetail: "plan-generation returned an empty plan");

        evt.EventType.Should().Be("CYCLE.STEP_FAILED");
        evt.Status.Should().Be("error", "a failed step must be a LOUD audit row, never a false success");
        evt.Tags!["issueId"].Should().Be("42");
        evt.Tags["issueNumber"].Should().Be("42");
        evt.Tags["repository"].Should().Be("owner/repo");
        evt.Tags["tenantId"].Should().Be(tenant.ToString("D"));
        evt.Tags["stepId"].Should().Be("plan-generation");
        evt.Data["stepId"].Should().Be("plan-generation");
        evt.Data["errorDetail"].Should().Be("plan-generation returned an empty plan");
    }

    [Test]
    public void BuildTammaEvent_Started_NoTenant_OmitsTenantTag_SuccessStatus()
    {
        var evt = EmitCycleEventActivity.BuildTammaEvent(
            CycleEvents.Started,
            issueNumber: 7,
            repository: "owner/repo",
            tenantId: null,           // single-user → platform-scope
            stepId: null,
            errorDetail: null);

        evt.Status.Should().Be("success");
        evt.Tags!.Should().NotContainKey("tenantId", "single-user cycle events are platform-scope");
        evt.Tags.Should().NotContainKey("stepId");
        evt.Data.Should().NotContainKey("errorDetail");
    }
}
