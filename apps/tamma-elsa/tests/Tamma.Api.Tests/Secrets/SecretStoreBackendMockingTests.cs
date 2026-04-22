using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Sanity test that confirms <see cref="ISecretStoreBackend"/> is
/// mockable via Moq — the AC9 promise that "xUnit test fixtures mock
/// ISecretStoreBackend so rotation / admin-service tests in later
/// stories do not require a real Postgres". A non-mockable interface
/// (e.g. accidentally sealed, accidentally reliant on a concrete type
/// in its method signatures) would make the rotation tests in 29-6 /
/// 29-7 / 29-8 painful, so we pin the seam here.
/// </summary>
[TestFixture]
public class SecretStoreBackendMockingTests
{
    private static readonly Guid SecretId = Guid.NewGuid();

    [Test]
    public async Task BackendCanBeMocked_AndCallsCanBeAsserted()
    {
        var mock = new Mock<ISecretStoreBackend>();

        mock.Setup(x => x.PutVersionAsync(
                SecretId, 1, "plaintext", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.GetVersionPlaintextAsync(
                SecretId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("plaintext");

        ISecretStoreBackend backend = mock.Object;

        await backend.PutVersionAsync(SecretId, 1, "plaintext");
        var fetched = await backend.GetVersionPlaintextAsync(SecretId, 1);

        fetched.Should().Be("plaintext");
        mock.Verify(x => x.PutVersionAsync(
            SecretId, 1, "plaintext", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(x => x.GetVersionPlaintextAsync(
            SecretId, 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AuditorCanBeMocked_AndEventCapturedForAssertion()
    {
        var captured = new List<SecretAuditEvent>();
        var mock = new Mock<ISecretAccessAuditor>();
        mock.Setup(x => x.EmitAsync(It.IsAny<SecretAuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns<SecretAuditEvent, CancellationToken>((evt, _) =>
            {
                captured.Add(evt);
                return Task.CompletedTask;
            });

        ISecretAccessAuditor auditor = mock.Object;
        var refId = SecretRef.ForTenant(Guid.NewGuid(), "db/role");
        await auditor.EmitAsync(new SecretAuditEvent(
            SecretAuditEventTypes.RotateStarted,
            refId,
            ActorUserId: Guid.NewGuid(),
            VersionNumber: 2,
            Outcome: SecretAuditOutcome.Success,
            Detail: null,
            OccurredAt: DateTimeOffset.UtcNow));

        captured.Should().HaveCount(1);
        captured[0].EventType.Should().Be(SecretAuditEventTypes.RotateStarted);
        captured[0].Reference.Should().Be(refId);
    }

    [Test]
    public void StoreCanBeMocked_AndReturnTypedMetadata()
    {
        // The high-level facade ISecretStore is the entry point for
        // future admin-endpoint tests in 29-4 / 29-5; pin that the
        // facade is mockable too, end-to-end.
        var mock = new Mock<ISecretStore>();
        var refId = SecretRef.ForPlatform("db/role");
        var meta = SecretMetadataFactory.Create(
            name: "db/role",
            scope: SecretScope.Platform,
            tenantId: null,
            purpose: SecretPurpose.DbCredential,
            consumerRefs: null,
            ownerUserId: Guid.NewGuid(),
            rotationSchedule: null,
            now: DateTimeOffset.UtcNow);

        mock.Setup(x => x.GetAsync(refId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(meta);

        var fetched = mock.Object.GetAsync(refId).GetAwaiter().GetResult();
        fetched.Should().Be(meta);
    }
}
