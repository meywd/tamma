using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Ambiguity;

namespace Tamma.Activities.Tests.Ambiguity;

/// <summary>
/// Story 3.6 — unit coverage for the AMBIGUITY.* event mapping
/// (<see cref="EmitAmbiguityEventActivity.BuildTammaEvent"/> +
/// <see cref="AmbiguityEvents.StatusForEvent"/>). Verifies the queryable DCB tags, the
/// per-transition data payload (including that a genuine score of 0.0 is still recorded), and —
/// critically — that the failed terminal is a LOUD error-status row (never a false success).
/// </summary>
[TestFixture]
public class EmitAmbiguityEventActivityTests
{
    private static readonly Guid Tenant = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Test]
    public void BuildTammaEvent_Scored_CarriesTagsAndData()
    {
        var evt = EmitAmbiguityEventActivity.BuildTammaEvent(
            AmbiguityEvents.Scored, "sess-1", "issue-9", Tenant,
            score: 0.72, ambiguityCount: 3, confidence: 0.8, threshold: 0d, detail: "done");

        evt.EventType.Should().Be("AMBIGUITY.SCORED");
        evt.Status.Should().Be("success");
        evt.Tags!["sessionId"].Should().Be("sess-1");
        evt.Tags["issueId"].Should().Be("issue-9");
        evt.Tags["tenantId"].Should().Be(Tenant.ToString("D"));
        evt.Data["score"].Should().Be(0.72);
        evt.Data["ambiguityCount"].Should().Be(3);
        evt.Data["confidence"].Should().Be(0.8);
        evt.Data["detail"].Should().Be("done");
        evt.Data.Should().NotContainKey("threshold", "threshold 0 is omitted on the scored event");
    }

    [Test]
    public void BuildTammaEvent_ScoreZero_IsStillRecorded()
    {
        var evt = EmitAmbiguityEventActivity.BuildTammaEvent(
            AmbiguityEvents.Scored, "sess-1", "issue-9", Tenant,
            score: 0.0, ambiguityCount: 0, confidence: 0d, threshold: 0d, detail: null);

        evt.Data.Should().ContainKey("score",
            "a genuine score of 0.0 (perfectly clear) must still be recorded — a nullable input " +
            "distinguishes it from 'not yet computed'");
        evt.Data["score"].Should().Be(0.0);
        evt.Data.Should().NotContainKey("ambiguityCount", "0 ambiguities is omitted");
    }

    [Test]
    public void BuildTammaEvent_ClarificationTriggered_CarriesThreshold()
    {
        var evt = EmitAmbiguityEventActivity.BuildTammaEvent(
            AmbiguityEvents.ClarificationTriggered, "sess-1", "issue-9", Tenant,
            score: 0.8, ambiguityCount: 2, confidence: 0d, threshold: 0.5, detail: null);

        evt.EventType.Should().Be("AMBIGUITY.CLARIFICATION_TRIGGERED");
        evt.Status.Should().Be("success", "a threshold decision is a normal audit row, not an error");
        evt.Data["threshold"].Should().Be(0.5);
        evt.Data["score"].Should().Be(0.8);
    }

    [Test]
    public void BuildTammaEvent_Failed_IsLoudErrorStatus()
    {
        var evt = EmitAmbiguityEventActivity.BuildTammaEvent(
            AmbiguityEvents.Failed, "sess-1", "issue-9", Tenant,
            score: null, ambiguityCount: 0, confidence: 0d, threshold: 0d, detail: "boom");

        evt.Status.Should().Be("error",
            "a failed scoring must be a LOUD error-status row, never a false success");
        evt.Data.Should().NotContainKey("score", "no score was computed on failure (null omitted)");
    }

    [Test]
    public void BuildTammaEvent_Started_IsStartedStatus_AndOmitsScore()
    {
        var evt = EmitAmbiguityEventActivity.BuildTammaEvent(
            AmbiguityEvents.Started, "sess-1", "issue-9", Tenant,
            score: null, ambiguityCount: 0, confidence: 0d, threshold: 0d, detail: null);

        evt.Status.Should().Be("started");
        evt.Data.Should().NotContainKey("score", "no score yet at start (null omitted)");
        evt.Data.Should().NotContainKey("detail", "a null detail is omitted");
    }

    [Test]
    public void BuildTammaEvent_NullTenant_OmitsTenantTag()
    {
        var evt = EmitAmbiguityEventActivity.BuildTammaEvent(
            AmbiguityEvents.Scored, "sess-1", "issue-9", tenantId: null,
            score: 0.5, ambiguityCount: 1, confidence: 0.5, threshold: 0d, detail: null);

        evt.Tags.Should().NotContainKey("tenantId",
            "single-user / platform-scope ambiguity events carry no tenantId tag");
    }

    [Test]
    public void ParseTenantId_RoundTrips_AndRejectsGarbage()
    {
        AmbiguityEvents.ParseTenantId(Tenant.ToString()).Should().Be(Tenant);
        AmbiguityEvents.ParseTenantId("").Should().BeNull();
        AmbiguityEvents.ParseTenantId("not-a-guid").Should().BeNull();
    }

    [TestCase("AMBIGUITY.STARTED", "started")]
    [TestCase("AMBIGUITY.SCORED", "success")]
    [TestCase("AMBIGUITY.CLARIFICATION_TRIGGERED", "success")]
    [TestCase("AMBIGUITY.BELOW_THRESHOLD", "success")]
    [TestCase("AMBIGUITY.FAILED", "error")]
    public void StatusForEvent_MapsCorrectly(string type, string expected)
    {
        AmbiguityEvents.StatusForEvent(type).Should().Be(expected);
    }
}
