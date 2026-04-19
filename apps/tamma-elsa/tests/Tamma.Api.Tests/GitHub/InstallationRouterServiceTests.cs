using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.GitHub;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.GitHub;

/// <summary>
/// Unit tests for <see cref="InstallationRouterService"/>, covering both the
/// OAuth callback path (user return from the install flow) and the webhook
/// dispatch path (installation + installation_repositories events).
/// </summary>
[TestFixture]
public class InstallationRouterServiceTests
{
    private Mock<IInstallationRepository> _installRepo = null!;
    private Mock<IEventRepository> _eventRepo = null!;
    private Mock<ITenantRepository> _tenantRepo = null!;
    private Mock<IUserRepository> _userRepo = null!;
    private Mock<ILogger<InstallationRouterService>> _logger = null!;
    private InstallationRouterService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _installRepo = new Mock<IInstallationRepository>();
        _eventRepo = new Mock<IEventRepository>();
        _tenantRepo = new Mock<ITenantRepository>();
        _userRepo = new Mock<IUserRepository>();
        _logger = new Mock<ILogger<InstallationRouterService>>();

        _service = new InstallationRouterService(
            _installRepo.Object,
            _eventRepo.Object,
            _tenantRepo.Object,
            _userRepo.Object,
            new MemoryCache(new MemoryCacheOptions()),
            _logger.Object);
    }

    // ─── HandleCallbackAsync ──────────────────────────────────────────────────

    [Test]
    public async Task HandleCallbackAsync_HappyPath_PersistsInstallationAndEmitsEvent()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        _userRepo.Setup(r => r.GetByIdAsync(userId))
            .ReturnsAsync(new User
            {
                Id = userId,
                Email = "test@example.com",
                TenantId = tenantId
            });

        _tenantRepo.Setup(r => r.GetByIdAsync(tenantId))
            .ReturnsAsync(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });

        _installRepo.Setup(r => r.GetByInstallationIdAsync(12345L))
            .ReturnsAsync((GitHubInstallation?)null);

        _installRepo.Setup(r => r.CreateAsync(It.IsAny<GitHubInstallation>()))
            .ReturnsAsync((GitHubInstallation i) => i);

        var result = await _service.HandleCallbackAsync(12345L, null, userId);

        result.Success.Should().BeTrue();
        result.TenantId.Should().Be(tenantId);
        result.InstallationId.Should().Be(12345L);

        _installRepo.Verify(r => r.CreateAsync(It.Is<GitHubInstallation>(
            i => i.InstallationId == 12345L && i.TenantId == tenantId)),
            Times.Once);
        _eventRepo.Verify(r => r.AppendAsync(It.Is<DomainEvent>(
            e => e.Type == "INSTALLATION.LINKED.SUCCESS")),
            Times.Once);
    }

    [Test]
    public async Task HandleCallbackAsync_UnknownUser_ReturnsError()
    {
        var userId = Guid.NewGuid();
        _userRepo.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync((User?)null);

        var result = await _service.HandleCallbackAsync(12345L, null, userId);

        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be("unknown_user");
        _installRepo.Verify(r => r.CreateAsync(It.IsAny<GitHubInstallation>()), Times.Never);
        _eventRepo.Verify(r => r.AppendAsync(It.IsAny<DomainEvent>()), Times.Never);
    }

    [Test]
    public async Task HandleCallbackAsync_UserWithoutActiveTenant_ReturnsError()
    {
        var userId = Guid.NewGuid();
        _userRepo.Setup(r => r.GetByIdAsync(userId))
            .ReturnsAsync(new User { Id = userId, Email = "t@e.com", TenantId = null });

        var result = await _service.HandleCallbackAsync(12345L, null, userId);

        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be("no_active_tenant");
    }

    [Test]
    public async Task HandleCallbackAsync_ExistingInstallation_UpsertsAndEmitsEvent()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var entityId = Guid.NewGuid();

        _userRepo.Setup(r => r.GetByIdAsync(userId))
            .ReturnsAsync(new User { Id = userId, Email = "t@e.com", TenantId = tenantId });
        _tenantRepo.Setup(r => r.GetByIdAsync(tenantId))
            .ReturnsAsync(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });

        _installRepo.Setup(r => r.GetByInstallationIdAsync(12345L))
            .ReturnsAsync(new GitHubInstallation
            {
                Id = entityId,
                InstallationId = 12345L,
                AccountLogin = "acme",
                AccountType = "Organization",
                TenantId = tenantId
            });

        _installRepo.Setup(r => r.UpsertAsync(It.IsAny<GitHubInstallation>()))
            .ReturnsAsync((GitHubInstallation i) => i);

        var result = await _service.HandleCallbackAsync(12345L, null, userId);

        result.Success.Should().BeTrue();
        _installRepo.Verify(r => r.UpsertAsync(It.IsAny<GitHubInstallation>()), Times.Once);
    }

    // ─── HandleWebhookAsync: installation events ──────────────────────────────

    [Test]
    public async Task HandleWebhookAsync_InstallationCreated_PersistsInstallation()
    {
        var payload = JsonDocument.Parse("""
            {
              "action": "created",
              "installation": {
                "id": 987654,
                "app_id": 42,
                "account": {
                  "login": "acme-org",
                  "type": "Organization"
                },
                "permissions": { "issues": "write" }
              }
            }
            """).RootElement;

        _installRepo.Setup(r => r.UpsertAsync(It.IsAny<GitHubInstallation>()))
            .ReturnsAsync((GitHubInstallation i) => { i.Id = Guid.NewGuid(); return i; });

        var result = await _service.HandleWebhookAsync("installation", payload);

        result.Skipped.Should().BeFalse();
        result.Action.Should().Be("created");

        _installRepo.Verify(r => r.UpsertAsync(It.Is<GitHubInstallation>(
            i => i.InstallationId == 987654L && i.AccountLogin == "acme-org")),
            Times.Once);
        _eventRepo.Verify(r => r.AppendAsync(It.Is<DomainEvent>(
            e => e.Type == "INSTALLATION.CREATED.SUCCESS")),
            Times.Once);
    }

    [Test]
    public async Task HandleWebhookAsync_InstallationDeleted_HardDeletesInstallation()
    {
        // Audit finding 030 — switched from soft-delete (which collided with
        // SuspendedAt) to hard-delete; audit is preserved by the
        // INSTALLATION.DELETED.SUCCESS event below.
        var payload = JsonDocument.Parse("""
            {
              "action": "deleted",
              "installation": {
                "id": 987654,
                "account": { "login": "acme", "type": "Organization" }
              }
            }
            """).RootElement;

        var result = await _service.HandleWebhookAsync("installation", payload);

        result.Skipped.Should().BeFalse();
        result.Action.Should().Be("deleted");
        _installRepo.Verify(r => r.DeleteAsync(987654L), Times.Once);
        _eventRepo.Verify(r => r.AppendAsync(It.Is<DomainEvent>(
            e => e.Type == "INSTALLATION.DELETED.SUCCESS")),
            Times.Once);
    }

    [Test]
    public async Task HandleWebhookAsync_InstallationSuspend_FlipsSuspendedFlag()
    {
        var payload = JsonDocument.Parse("""
            {
              "action": "suspend",
              "installation": {
                "id": 12345,
                "account": { "login": "acme", "type": "Organization" }
              }
            }
            """).RootElement;

        var result = await _service.HandleWebhookAsync("installation", payload);

        result.Skipped.Should().BeFalse();
        result.Action.Should().Be("suspend");
        _installRepo.Verify(r => r.SetSuspendedAsync(12345L, true), Times.Once);
    }

    [Test]
    public async Task HandleWebhookAsync_InstallationUnsuspend_FlipsSuspendedFlag()
    {
        var payload = JsonDocument.Parse("""
            {
              "action": "unsuspend",
              "installation": {
                "id": 12345,
                "account": { "login": "acme", "type": "Organization" }
              }
            }
            """).RootElement;

        var result = await _service.HandleWebhookAsync("installation", payload);

        result.Skipped.Should().BeFalse();
        _installRepo.Verify(r => r.SetSuspendedAsync(12345L, false), Times.Once);
    }

    // ─── HandleWebhookAsync: installation_repositories ────────────────────────

    [Test]
    public async Task HandleWebhookAsync_ReposAdded_InsertsRepos()
    {
        var entityId = Guid.NewGuid();
        _installRepo.Setup(r => r.GetByInstallationIdAsync(12345L))
            .ReturnsAsync(new GitHubInstallation
            {
                Id = entityId,
                InstallationId = 12345L,
                AccountLogin = "acme",
                AccountType = "Organization"
            });

        var payload = JsonDocument.Parse("""
            {
              "action": "added",
              "installation": { "id": 12345 },
              "repositories_added": [
                { "id": 1, "full_name": "acme/repo1" },
                { "id": 2, "full_name": "acme/repo2" }
              ]
            }
            """).RootElement;

        var result = await _service.HandleWebhookAsync("installation_repositories", payload);

        result.Skipped.Should().BeFalse();
        _installRepo.Verify(r => r.AddRepoAsync(entityId, 1L, "acme/repo1"), Times.Once);
        _installRepo.Verify(r => r.AddRepoAsync(entityId, 2L, "acme/repo2"), Times.Once);
    }

    [Test]
    public async Task HandleWebhookAsync_ReposRemoved_SoftDeletesRepos()
    {
        var entityId = Guid.NewGuid();
        _installRepo.Setup(r => r.GetByInstallationIdAsync(12345L))
            .ReturnsAsync(new GitHubInstallation
            {
                Id = entityId,
                InstallationId = 12345L,
                AccountLogin = "acme",
                AccountType = "Organization"
            });

        var payload = JsonDocument.Parse("""
            {
              "action": "removed",
              "installation": { "id": 12345 },
              "repositories_removed": [
                { "id": 3, "full_name": "acme/repo3" }
              ]
            }
            """).RootElement;

        var result = await _service.HandleWebhookAsync("installation_repositories", payload);

        result.Skipped.Should().BeFalse();
        _installRepo.Verify(r => r.RemoveRepoAsync(entityId, 3L), Times.Once);
    }

    [Test]
    public async Task HandleWebhookAsync_ReposAdded_UnknownInstallation_Skips()
    {
        _installRepo.Setup(r => r.GetByInstallationIdAsync(99999L))
            .ReturnsAsync((GitHubInstallation?)null);

        var payload = JsonDocument.Parse("""
            {
              "action": "added",
              "installation": { "id": 99999 },
              "repositories_added": [{ "id": 1, "full_name": "a/b" }]
            }
            """).RootElement;

        var result = await _service.HandleWebhookAsync("installation_repositories", payload);

        // Unknown installation -> we skip quietly rather than crashing
        result.Skipped.Should().BeTrue();
        _installRepo.Verify(
            r => r.AddRepoAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<string>()),
            Times.Never);
    }

    // ─── HandleWebhookAsync: ignored / unknown events ─────────────────────────

    [Test]
    public async Task HandleWebhookAsync_UnknownEvent_ReturnsSkipped()
    {
        var payload = JsonDocument.Parse("""{ "action": "opened" }""").RootElement;

        var result = await _service.HandleWebhookAsync("pull_request", payload);

        result.Skipped.Should().BeTrue();
        result.EventType.Should().Be("pull_request");
        _installRepo.VerifyNoOtherCalls();
        _eventRepo.VerifyNoOtherCalls();
    }

    [Test]
    public async Task HandleWebhookAsync_InstallationWithUnknownAction_ReturnsSkipped()
    {
        var payload = JsonDocument.Parse("""
            {
              "action": "new_permissions_accepted",
              "installation": {
                "id": 12345,
                "account": { "login": "acme", "type": "Organization" }
              }
            }
            """).RootElement;

        var result = await _service.HandleWebhookAsync("installation", payload);

        result.Skipped.Should().BeTrue();
        result.Action.Should().Be("new_permissions_accepted");
    }

    [Test]
    public async Task HandleWebhookAsync_InstallationCreatedWithRepositories_InsertsRepos()
    {
        var payload = JsonDocument.Parse("""
            {
              "action": "created",
              "installation": {
                "id": 987654,
                "app_id": 42,
                "account": { "login": "acme", "type": "Organization" }
              },
              "repositories": [
                { "id": 10, "full_name": "acme/repo-a" },
                { "id": 20, "full_name": "acme/repo-b" }
              ]
            }
            """).RootElement;

        var entityId = Guid.NewGuid();
        _installRepo.Setup(r => r.UpsertAsync(It.IsAny<GitHubInstallation>()))
            .ReturnsAsync((GitHubInstallation i) => { i.Id = entityId; return i; });

        var result = await _service.HandleWebhookAsync("installation", payload);

        result.Skipped.Should().BeFalse();
        _installRepo.Verify(r => r.AddRepoAsync(entityId, 10L, "acme/repo-a"), Times.Once);
        _installRepo.Verify(r => r.AddRepoAsync(entityId, 20L, "acme/repo-b"), Times.Once);
    }
}
