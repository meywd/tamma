using System.Text.Json;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.Tests;

/// <summary>
/// Story 31-2 Step 6 — emitter tests covering AC7 event types
/// (CONNECTED / DISCONNECTED / CREDENTIAL_ROTATED) and their tag
/// shape. Round-trips the JSON tags so a downstream listener (the
/// cache invalidator, analytics) can rely on the field names.
/// </summary>
[TestFixture]
public class PlatformInstallationEventEmitterTests
{
    private Mock<IPlatformEventRepository> _events = null!;
    private PlatformInstallationEventEmitter _emitter = null!;
    private List<PlatformEvent> _captured = null!;

    [SetUp]
    public void SetUp()
    {
        _captured = new List<PlatformEvent>();
        _events = new Mock<IPlatformEventRepository>(MockBehavior.Strict);
        _events
            .Setup(e => e.AppendAsync(It.IsAny<PlatformEvent>(), It.IsAny<CancellationToken>()))
            .Returns<PlatformEvent, CancellationToken>((evt, _) =>
            {
                _captured.Add(evt);
                return Task.FromResult<PlatformEvent?>(evt);
            });

        _emitter = new PlatformInstallationEventEmitter(_events.Object);
    }

    [Test]
    public async Task EmitConnectedAsync_WritesExpectedShape()
    {
        var tenantId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        await _emitter.EmitConnectedAsync(
            tenantId, PlatformKind.GitHub, rowId,
            installationExternalId: "12345",
            actorUserId: actorId);

        _captured.Should().HaveCount(1);
        var evt = _captured[0];
        evt.Type.Should().Be(PlatformInstallationEventTypes.Connected);
        evt.TenantId.Should().Be(tenantId);
        evt.UserId.Should().Be(actorId);

        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.GetProperty("tenantId").GetString().Should().Be(tenantId.ToString());
        tags.RootElement.GetProperty("platformKind").GetString().Should().Be("github");
        tags.RootElement.GetProperty("installationId").GetString().Should().Be(rowId.ToString());
        tags.RootElement.GetProperty("installationExternalId").GetString().Should().Be("12345");
    }

    [Test]
    public async Task EmitDisconnectedAsync_UsesDisconnectedType()
    {
        await _emitter.EmitDisconnectedAsync(
            Guid.NewGuid(), PlatformKind.Gitea, Guid.NewGuid(),
            installationExternalId: null,
            actorUserId: null);

        _captured.Should().HaveCount(1);
        _captured[0].Type.Should().Be(PlatformInstallationEventTypes.Disconnected);
    }

    [Test]
    public async Task EmitCredentialRotatedAsync_OmitsExternalIdTag()
    {
        // Rotation events don't carry external id (rotation is a
        // tenant-side concern; the external id doesn't change).
        await _emitter.EmitCredentialRotatedAsync(
            Guid.NewGuid(), PlatformKind.GitLab, Guid.NewGuid(),
            actorUserId: null);

        _captured.Should().HaveCount(1);
        var evt = _captured[0];
        evt.Type.Should().Be(PlatformInstallationEventTypes.CredentialRotated);

        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.TryGetProperty("installationExternalId", out _)
            .Should().BeFalse();
    }

    [Test]
    public async Task EmitConnectedAsync_NullExternalId_OmittedFromTags()
    {
        await _emitter.EmitConnectedAsync(
            Guid.NewGuid(), PlatformKind.Bitbucket, Guid.NewGuid(),
            installationExternalId: null,
            actorUserId: null);

        using var tags = JsonDocument.Parse(_captured[0].Tags);
        tags.RootElement.TryGetProperty("installationExternalId", out _)
            .Should().BeFalse();
    }

    [Test]
    public async Task Emit_OnAppendFailure_DoesNotThrow()
    {
        // Audit failures must not block lifecycle progression — the
        // emitter swallows + logs, the caller continues.
        _events
            .Setup(e => e.AppendAsync(It.IsAny<PlatformEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        var act = async () => await _emitter.EmitConnectedAsync(
            Guid.NewGuid(), PlatformKind.GitHub, Guid.NewGuid(),
            installationExternalId: null,
            actorUserId: null);

        await act.Should().NotThrowAsync();
    }
}

