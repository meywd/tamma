using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Decomposition;

namespace Tamma.Activities.Tests.Decomposition;

/// <summary>
/// Story 2.14 — unit coverage for the DECOMPOSITION.* event mapping
/// (<see cref="EmitDecompositionEventActivity.BuildTammaEvent"/> +
/// <see cref="DecompositionEvents.StatusForEvent"/>). Verifies the queryable DCB tags, the
/// per-transition data payload, and — critically — that the failed terminal is a LOUD
/// error-status row (never a false success).
/// </summary>
[TestFixture]
public class EmitDecompositionEventActivityTests
{
    private static readonly Guid Tenant = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Test]
    public void BuildTammaEvent_Completed_CarriesTagsAndData()
    {
        var evt = EmitDecompositionEventActivity.BuildTammaEvent(
            DecompositionEvents.Completed, "sess-1", "issue-137", Tenant, subtaskCount: 4, detail: "done");

        evt.EventType.Should().Be("DECOMPOSITION.COMPLETED");
        evt.Status.Should().Be("success");
        evt.Tags!["sessionId"].Should().Be("sess-1");
        evt.Tags["issueId"].Should().Be("issue-137");
        evt.Tags["tenantId"].Should().Be(Tenant.ToString("D"));
        evt.Data["subtaskCount"].Should().Be(4);
        evt.Data["detail"].Should().Be("done");
    }

    [Test]
    public void BuildTammaEvent_Failed_IsLoudErrorStatus()
    {
        var evt = EmitDecompositionEventActivity.BuildTammaEvent(
            DecompositionEvents.Failed, "sess-1", "issue-137", Tenant, subtaskCount: 0, detail: "boom");

        evt.Status.Should().Be("error",
            "a failed decomposition must be a LOUD error-status row, never a false success");
    }

    [Test]
    public void BuildTammaEvent_Started_IsStartedStatus_AndOmitsEmptyPayload()
    {
        var evt = EmitDecompositionEventActivity.BuildTammaEvent(
            DecompositionEvents.Started, "sess-1", "issue-137", Tenant, subtaskCount: 0, detail: null);

        evt.Status.Should().Be("started");
        evt.Data.Should().NotContainKey("subtaskCount", "no sub-tasks yet at start (count 0 is omitted)");
        evt.Data.Should().NotContainKey("detail", "a null detail is omitted");
    }

    [Test]
    public void BuildTammaEvent_NullTenant_OmitsTenantTag()
    {
        var evt = EmitDecompositionEventActivity.BuildTammaEvent(
            DecompositionEvents.Completed, "sess-1", "issue-137", tenantId: null, subtaskCount: 2, detail: null);

        evt.Tags.Should().NotContainKey("tenantId",
            "single-user / platform-scope decomposition events carry no tenantId tag");
    }

    [Test]
    public void ParseTenantId_RoundTrips_AndRejectsGarbage()
    {
        DecompositionEvents.ParseTenantId(Tenant.ToString()).Should().Be(Tenant);
        DecompositionEvents.ParseTenantId("").Should().BeNull();
        DecompositionEvents.ParseTenantId("not-a-guid").Should().BeNull();
    }

    [TestCase("DECOMPOSITION.STARTED", "started")]
    [TestCase("DECOMPOSITION.CONTEXT_GATHERED", "success")]
    [TestCase("DECOMPOSITION.COMPLETED", "success")]
    [TestCase("DECOMPOSITION.FAILED", "error")]
    public void StatusForEvent_MapsCorrectly(string type, string expected)
    {
        DecompositionEvents.StatusForEvent(type).Should().Be(expected);
    }
}
