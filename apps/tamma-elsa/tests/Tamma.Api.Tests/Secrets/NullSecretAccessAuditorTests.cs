using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Tests for <see cref="NullSecretAccessAuditor"/>. Trivial — confirms
/// the no-op auditor swallows events without throwing for every
/// canonical event type. Story 29-2 swaps in the real Postgres-backed
/// auditor; this test set guards the seam.
/// </summary>
[TestFixture]
public class NullSecretAccessAuditorTests
{
    [Test]
    public async Task EmitAsync_AcceptsCanonicalEventTypes()
    {
        var auditor = new NullSecretAccessAuditor();
        var refId = SecretRef.ForPlatform("db/role");

        var canonicalTypes = new[]
        {
            SecretAuditEventTypes.Read,
            SecretAuditEventTypes.Write,
            SecretAuditEventTypes.RotateStarted,
            SecretAuditEventTypes.RotateSucceeded,
            SecretAuditEventTypes.RotateFailed,
            SecretAuditEventTypes.Reveal,
            SecretAuditEventTypes.VersionRevoked,
        };

        foreach (var type in canonicalTypes)
        {
            await auditor.EmitAsync(new SecretAuditEvent(
                EventType: type,
                Reference: refId,
                ActorUserId: Guid.NewGuid(),
                VersionNumber: 1,
                Outcome: SecretAuditOutcome.Success,
                Detail: null,
                OccurredAt: DateTimeOffset.UtcNow));
        }
    }

    [Test]
    public void CanonicalEventTypeStrings_MatchAcSpec()
    {
        // Pin the AC5 vocabulary so a typo would surface in CI rather
        // than break a downstream alert rule.
        SecretAuditEventTypes.Read.Should().Be("SECRET.READ");
        SecretAuditEventTypes.Write.Should().Be("SECRET.WRITE");
        SecretAuditEventTypes.RotateStarted.Should().Be("SECRET.ROTATE.STARTED");
        SecretAuditEventTypes.RotateSucceeded.Should().Be("SECRET.ROTATE.SUCCESS");
        SecretAuditEventTypes.RotateFailed.Should().Be("SECRET.ROTATE.FAILED");
        SecretAuditEventTypes.Reveal.Should().Be("SECRET.REVEAL");
        SecretAuditEventTypes.VersionRevoked.Should().Be("SECRET.VERSION.REVOKED");
    }

    [Test]
    public async Task EmitAsync_HonoursCancellationToken()
    {
        // Even the null impl must accept a cancellation token so the
        // signature stays consistent with Story 29-2's real
        // persistence-backed auditor.
        var auditor = new NullSecretAccessAuditor();
        using var cts = new CancellationTokenSource();
        await auditor.EmitAsync(
            new SecretAuditEvent(
                SecretAuditEventTypes.Read,
                SecretRef.ForPlatform("a/b"),
                Guid.Empty,
                null,
                SecretAuditOutcome.Success,
                null,
                DateTimeOffset.UtcNow),
            cts.Token);
    }
}
