using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Activities.Tests.SecretsRotation;

/// <summary>
/// Story 29-6 — baseline value-type tests for the rotation contract
/// types. Ensures factory methods / convenience helpers behave as
/// advertised so handler implementations can rely on them.
/// </summary>
[TestFixture]
public class RotationContractTests
{
    [Test]
    public void ProbeResult_Healthy_FactoryIsHealthy()
    {
        var result = ProbeResult.Healthy(42);
        result.Status.Should().Be(ProbeStatus.Healthy);
        result.IsHealthy.Should().BeTrue();
        result.Reason.Should().BeEmpty();
        result.DurationMs.Should().Be(42);
    }

    [Test]
    public void ProbeResult_Unhealthy_FactoryPreservesReason()
    {
        var result = ProbeResult.Unhealthy("connection_refused", 103);
        result.Status.Should().Be(ProbeStatus.Unhealthy);
        result.IsHealthy.Should().BeFalse();
        result.Reason.Should().Be("connection_refused");
        result.DurationMs.Should().Be(103);
    }

    [Test]
    public void RotationContext_ForCorrelation_DefaultsOperatorAndDryRun()
    {
        var ctx = RotationContext.ForCorrelation("rot_abc");
        ctx.RotationCorrelationId.Should().Be("rot_abc");
        ctx.OperatorUserId.Should().Be(Guid.Empty);
        ctx.DryRun.Should().BeFalse();
        ctx.HandlerOptions.Should().BeEmpty();
    }

    [Test]
    public void RotationContext_GetOption_ReturnsDefaultWhenMissing()
    {
        var ctx = RotationContext.ForCorrelation("rot_abc");
        ctx.GetOption("MissingKey", "fallback").Should().Be("fallback");
    }

    [Test]
    public void RotationContext_GetOption_ReadsPresent()
    {
        var ctx = new RotationContext(
            "rot_abc",
            Guid.Empty,
            DryRun: false,
            new Dictionary<string, string> { ["key"] = "value" });
        ctx.GetOption("key", "fallback").Should().Be("value");
    }

    [Test]
    public void RotationAuditEvent_Create_StampsTimestampAndEmptyDataMap()
    {
        var secretId = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;
        var evt = RotationAuditEvent.Create(
            RotationAuditEvents.Staged,
            secretId,
            tenantId: null,
            rotationCorrelationId: "rot_test",
            versionNumber: 3);
        var after = DateTimeOffset.UtcNow;

        evt.EventType.Should().Be(RotationAuditEvents.Staged);
        evt.SecretId.Should().Be(secretId);
        evt.TenantId.Should().BeNull();
        evt.RotationCorrelationId.Should().Be("rot_test");
        evt.VersionNumber.Should().Be(3);
        evt.Detail.Should().BeNull();
        evt.Data.Should().BeEmpty();
        evt.OccurredAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Test]
    public void RotationAuditEvents_Constants_AreStableCanonicalStrings()
    {
        RotationAuditEvents.Started.Should().Be("SECRET.ROTATION.STARTED");
        RotationAuditEvents.Staged.Should().Be("SECRET.ROTATION.STAGED");
        RotationAuditEvents.PushSuccess.Should().Be("SECRET.ROTATION.PUSH.SUCCESS");
        RotationAuditEvents.PushFailed.Should().Be("SECRET.ROTATION.PUSH.FAILED");
        RotationAuditEvents.ProbeSuccess.Should().Be("SECRET.ROTATION.PROBE.SUCCESS");
        RotationAuditEvents.ProbeFailed.Should().Be("SECRET.ROTATION.PROBE.FAILED");
        RotationAuditEvents.Switched.Should().Be("SECRET.ROTATION.SWITCHED");
        RotationAuditEvents.Activated.Should().Be("SECRET.ROTATION.ACTIVATED");
        RotationAuditEvents.Completed.Should().Be("SECRET.ROTATION.COMPLETED");
        RotationAuditEvents.Failed.Should().Be("SECRET.ROTATION.FAILED");
        RotationAuditEvents.VersionRetired.Should().Be("SECRET.VERSION.RETIRED");
        RotationAuditEvents.CompensationStarted.Should().Be("SECRET.ROTATION.COMPENSATION.STARTED");
        RotationAuditEvents.CompensationSuccess.Should().Be("SECRET.ROTATION.COMPENSATION.SUCCESS");
        RotationAuditEvents.CompensationFailed.Should().Be("SECRET.ROTATION.COMPENSATION.FAILED");
    }

    [Test]
    public void RotationTarget_Record_EqualityByValue()
    {
        var a = new RotationTarget(Guid.Empty, "x", null, "postgres", "role=app", 2, 1);
        var b = new RotationTarget(Guid.Empty, "x", null, "postgres", "role=app", 2, 1);
        a.Should().Be(b);
    }
}
