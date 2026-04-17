using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.SaaS;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.SaaS;

/// <summary>
/// Unit tests for <see cref="ApiKeyRotationService"/>. Covers the happy path
/// (new key persisted + event emitted), authorization (only an installation's
/// tenant owner/admin can rotate), and unknown-installation handling.
/// </summary>
[TestFixture]
public class ApiKeyRotationServiceTests
{
    private Mock<IInstallationRepository> _installRepo = null!;
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private Mock<ITenantMembershipRepository> _membershipRepo = null!;
    private Mock<IEventRepository> _eventRepo = null!;
    private Mock<ILogger<ApiKeyRotationService>> _logger = null!;
    private ApiKeyRotationService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _installRepo = new Mock<IInstallationRepository>();
        _apiKeyRepo = new Mock<IApiKeyRepository>();
        _membershipRepo = new Mock<ITenantMembershipRepository>();
        _eventRepo = new Mock<IEventRepository>();
        _logger = new Mock<ILogger<ApiKeyRotationService>>();

        _service = new ApiKeyRotationService(
            _installRepo.Object,
            _apiKeyRepo.Object,
            _membershipRepo.Object,
            _eventRepo.Object,
            _logger.Object);
    }

    // ─── Happy path ─────────────────────────────────────────────────────────

    [Test]
    public async Task RotateAsync_HappyPath_CreatesNewKeyAndEmitsEvent()
    {
        var installationEntityId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();

        _installRepo.Setup(r => r.GetByEntityIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new GitHubInstallation
            {
                Id = installationEntityId,
                InstallationId = 12345L,
                AccountLogin = "acme",
                AccountType = "Organization",
                TenantId = tenantId
            });

        _membershipRepo.Setup(r => r.GetRoleAsync(tenantId, callerUserId))
            .ReturnsAsync("owner");

        // No existing installation-scoped key → service creates a fresh one
        _apiKeyRepo.Setup(r => r.ListByOwnerAsync(installationEntityId.ToString()))
            .ReturnsAsync(new List<ApiKey>());

        _apiKeyRepo.Setup(r => r.CreateAsync(It.IsAny<ApiKey>()))
            .ReturnsAsync((ApiKey k) =>
            {
                k.Id = Guid.NewGuid();
                return k;
            });

        var result = await _service.RotateAsync(installationEntityId, callerUserId);

        result.Success.Should().BeTrue();
        result.ErrorReason.Should().BeNull();
        result.PlaintextKey.Should().NotBeNullOrWhiteSpace();
        result.PlaintextKey!.Should().StartWith("tamma_sk_");
        result.KeyPrefix.Should().NotBeNullOrWhiteSpace();
        result.KeyId.Should().NotBeNull();

        _apiKeyRepo.Verify(r => r.CreateAsync(It.Is<ApiKey>(
            k => k.Scope == "installation" &&
                 k.OwnerId == installationEntityId.ToString() &&
                 k.TenantId == tenantId &&
                 !string.IsNullOrWhiteSpace(k.KeyHash) &&
                 !string.IsNullOrWhiteSpace(k.KeyPrefix))),
            Times.Once);

        _eventRepo.Verify(r => r.AppendAsync(It.Is<DomainEvent>(
            e => e.Type == "API_KEY.ROTATED" && e.TenantId == tenantId)),
            Times.Once);
    }

    [Test]
    public async Task RotateAsync_ExistingKey_RevokesOldAndReturnsNew()
    {
        var installationEntityId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();
        var existingKeyId = Guid.NewGuid();

        _installRepo.Setup(r => r.GetByEntityIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new GitHubInstallation
            {
                Id = installationEntityId,
                TenantId = tenantId,
                AccountLogin = "x",
                AccountType = "User"
            });

        _membershipRepo.Setup(r => r.GetRoleAsync(tenantId, callerUserId))
            .ReturnsAsync("admin");

        _apiKeyRepo.Setup(r => r.ListByOwnerAsync(installationEntityId.ToString()))
            .ReturnsAsync(new List<ApiKey>
            {
                new ApiKey
                {
                    Id = existingKeyId,
                    Scope = "installation",
                    OwnerId = installationEntityId.ToString(),
                    KeyHash = "oldhash",
                    KeyPrefix = "tamma_sk_old1234",
                    Label = "installation-key",
                    TenantId = tenantId
                }
            });

        _apiKeyRepo.Setup(r => r.RotateAsync(existingKeyId, It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((Guid _, string hash, string prefix) => new ApiKey
            {
                Id = Guid.NewGuid(),
                Scope = "installation",
                OwnerId = installationEntityId.ToString(),
                KeyHash = hash,
                KeyPrefix = prefix,
                Label = "installation-key",
                TenantId = tenantId,
                RotatedFromId = existingKeyId
            });

        var result = await _service.RotateAsync(installationEntityId, callerUserId);

        result.Success.Should().BeTrue();
        _apiKeyRepo.Verify(
            r => r.RotateAsync(existingKeyId, It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
        _apiKeyRepo.Verify(r => r.CreateAsync(It.IsAny<ApiKey>()), Times.Never);
        _eventRepo.Verify(r => r.AppendAsync(It.Is<DomainEvent>(
            e => e.Type == "API_KEY.ROTATED")), Times.Once);
    }

    // ─── Authorization ──────────────────────────────────────────────────────

    [Test]
    public async Task RotateAsync_CallerIsMember_NotOwnerOrAdmin_Rejected()
    {
        var installationEntityId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();

        _installRepo.Setup(r => r.GetByEntityIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new GitHubInstallation
            {
                Id = installationEntityId,
                TenantId = tenantId,
                AccountLogin = "x",
                AccountType = "User"
            });

        _membershipRepo.Setup(r => r.GetRoleAsync(tenantId, callerUserId))
            .ReturnsAsync("member");

        var result = await _service.RotateAsync(installationEntityId, callerUserId);

        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be("forbidden");
        result.PlaintextKey.Should().BeNull();

        _apiKeyRepo.Verify(r => r.CreateAsync(It.IsAny<ApiKey>()), Times.Never);
        _apiKeyRepo.Verify(
            r => r.RotateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        _eventRepo.Verify(r => r.AppendAsync(It.IsAny<DomainEvent>()), Times.Never);
    }

    [Test]
    public async Task RotateAsync_CallerHasNoMembership_Rejected()
    {
        var installationEntityId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();

        _installRepo.Setup(r => r.GetByEntityIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new GitHubInstallation
            {
                Id = installationEntityId,
                TenantId = tenantId,
                AccountLogin = "x",
                AccountType = "User"
            });

        _membershipRepo.Setup(r => r.GetRoleAsync(tenantId, callerUserId))
            .ReturnsAsync((string?)null);

        var result = await _service.RotateAsync(installationEntityId, callerUserId);

        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be("forbidden");
        _apiKeyRepo.Verify(r => r.CreateAsync(It.IsAny<ApiKey>()), Times.Never);
    }

    // ─── Not found ──────────────────────────────────────────────────────────

    [Test]
    public async Task RotateAsync_UnknownInstallation_ReturnsNotFound()
    {
        var installationEntityId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();

        _installRepo.Setup(r => r.GetByEntityIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync((GitHubInstallation?)null);

        var result = await _service.RotateAsync(installationEntityId, callerUserId);

        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be("not_found");
        result.PlaintextKey.Should().BeNull();
        _apiKeyRepo.Verify(r => r.CreateAsync(It.IsAny<ApiKey>()), Times.Never);
        _eventRepo.Verify(r => r.AppendAsync(It.IsAny<DomainEvent>()), Times.Never);
    }

    [Test]
    public async Task RotateAsync_InstallationWithoutTenant_Rejected()
    {
        var installationEntityId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();

        _installRepo.Setup(r => r.GetByEntityIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new GitHubInstallation
            {
                Id = installationEntityId,
                TenantId = null,
                AccountLogin = "x",
                AccountType = "User"
            });

        var result = await _service.RotateAsync(installationEntityId, callerUserId);

        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be("no_tenant");
        _apiKeyRepo.Verify(r => r.CreateAsync(It.IsAny<ApiKey>()), Times.Never);
    }

    [Test]
    public async Task RotateAsync_SuspendedInstallation_Rejected()
    {
        var installationEntityId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();

        _installRepo.Setup(r => r.GetByEntityIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new GitHubInstallation
            {
                Id = installationEntityId,
                TenantId = tenantId,
                SuspendedAt = DateTime.UtcNow,
                AccountLogin = "x",
                AccountType = "User"
            });

        var result = await _service.RotateAsync(installationEntityId, callerUserId);

        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be("suspended");
        _apiKeyRepo.Verify(r => r.CreateAsync(It.IsAny<ApiKey>()), Times.Never);
    }

    // ─── Event payload shape ────────────────────────────────────────────────

    [Test]
    public async Task RotateAsync_EventPayload_IncludesKeyPrefixNotPlaintext()
    {
        var installationEntityId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();

        _installRepo.Setup(r => r.GetByEntityIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new GitHubInstallation
            {
                Id = installationEntityId,
                TenantId = tenantId,
                AccountLogin = "x",
                AccountType = "User"
            });
        _membershipRepo.Setup(r => r.GetRoleAsync(tenantId, callerUserId))
            .ReturnsAsync("owner");
        _apiKeyRepo.Setup(r => r.ListByOwnerAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<ApiKey>());
        _apiKeyRepo.Setup(r => r.CreateAsync(It.IsAny<ApiKey>()))
            .ReturnsAsync((ApiKey k) => { k.Id = Guid.NewGuid(); return k; });

        DomainEvent? captured = null;
        _eventRepo.Setup(r => r.AppendAsync(It.IsAny<DomainEvent>()))
            .Callback<DomainEvent>(e => captured = e)
            .ReturnsAsync((DomainEvent e) => e);

        var result = await _service.RotateAsync(installationEntityId, callerUserId);
        result.Success.Should().BeTrue();

        captured.Should().NotBeNull();
        var dataRoot = JsonDocument.Parse(captured!.Data).RootElement;
        dataRoot.GetProperty("keyPrefix").GetString().Should().Be(result.KeyPrefix);
        dataRoot.TryGetProperty("plaintextKey", out _).Should().BeFalse(
            "raw key must never be persisted in event data");
    }
}
