using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Research;

namespace Tamma.Activities.Tests.Research;

/// <summary>
/// Story 3.4 — unit coverage for the RESEARCH.* event mapping
/// (<see cref="EmitResearchEventActivity.BuildTammaEvent"/> +
/// <see cref="ResearchEvents.StatusForEvent"/>). Verifies the queryable DCB tags, the
/// per-transition data payload, and — critically — that the failed terminal is a LOUD
/// error-status row (never a false success).
/// </summary>
[TestFixture]
public class EmitResearchEventActivityTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Test]
    public void BuildTammaEvent_Completed_CarriesTagsAndData()
    {
        var evt = EmitResearchEventActivity.BuildTammaEvent(
            ResearchEvents.Completed, "sess-1", "issue-9", Tenant, findingCount: 3, confidence: 0.82, detail: "done");

        evt.EventType.Should().Be("RESEARCH.COMPLETED");
        evt.Status.Should().Be("success");
        evt.Tags!["sessionId"].Should().Be("sess-1");
        evt.Tags["issueId"].Should().Be("issue-9");
        evt.Tags["tenantId"].Should().Be(Tenant.ToString("D"));
        evt.Data["findingCount"].Should().Be(3);
        evt.Data["confidence"].Should().Be(0.82);
        evt.Data["detail"].Should().Be("done");
    }

    [Test]
    public void BuildTammaEvent_Failed_IsLoudErrorStatus()
    {
        var evt = EmitResearchEventActivity.BuildTammaEvent(
            ResearchEvents.Failed, "sess-1", "issue-9", Tenant, findingCount: 0, confidence: 0d, detail: "boom");

        evt.Status.Should().Be("error",
            "a failed synthesis must be a LOUD error-status row, never a false success");
    }

    [Test]
    public void BuildTammaEvent_Started_IsStartedStatus()
    {
        var evt = EmitResearchEventActivity.BuildTammaEvent(
            ResearchEvents.Started, "sess-1", "issue-9", Tenant, findingCount: 0, confidence: 0d, detail: null);

        evt.Status.Should().Be("started");
        evt.Data.Should().NotContainKey("findingCount", "no findings yet at start (count 0 is omitted)");
        evt.Data.Should().NotContainKey("detail", "a null detail is omitted");
    }

    [Test]
    public void BuildTammaEvent_NullTenant_OmitsTenantTag()
    {
        var evt = EmitResearchEventActivity.BuildTammaEvent(
            ResearchEvents.Completed, "sess-1", "issue-9", tenantId: null, findingCount: 1, confidence: 0.5, detail: null);

        evt.Tags.Should().NotContainKey("tenantId",
            "single-user / platform-scope research events carry no tenantId tag");
    }

    [Test]
    public void ParseTenantId_RoundTrips_AndRejectsGarbage()
    {
        ResearchEvents.ParseTenantId(Tenant.ToString()).Should().Be(Tenant);
        ResearchEvents.ParseTenantId("").Should().BeNull();
        ResearchEvents.ParseTenantId("not-a-guid").Should().BeNull();
    }

    [TestCase("RESEARCH.STARTED", "started")]
    [TestCase("RESEARCH.CONTEXT_GATHERED", "success")]
    [TestCase("RESEARCH.COMPLETED", "success")]
    [TestCase("RESEARCH.FAILED", "error")]
    public void StatusForEvent_MapsCorrectly(string type, string expected)
    {
        ResearchEvents.StatusForEvent(type).Should().Be(expected);
    }
}
