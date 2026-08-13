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
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private Mock<Tamma.Platforms.GitHub.IGitHubAppInstallationReader> _gitHubApp = null!;
    private Mock<IInstallationSecretsPusher> _provisioner = null!;
    private Mock<ILogger<InstallationRouterService>> _logger = null!;
    private InstallationRouterService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _installRepo = new Mock<IInstallationRepository>();
        _eventRepo = new Mock<IEventRepository>();
        _tenantRepo = new Mock<ITenantRepository>();
        _userRepo = new Mock<IUserRepository>();
        _apiKeyRepo = new Mock<IApiKeyRepository>();
        _gitHubApp = new Mock<Tamma.Platforms.GitHub.IGitHubAppInstallationReader>();
        _provisioner = new Mock<IInstallationSecretsPusher>();
        _logger = new Mock<ILogger<InstallationRouterService>>();

        // Default: GitHub App client is unwired (Null behaviour) and the
        // provisioner returns no work so the callback unit tests focus on
        // tenant linking semantics. Findings 007 + 008 + 013 are exercised
        // separately via integration tests.
        _gitHubApp
            .Setup(c => c.GetInstallationAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Tamma.Platforms.GitHub.GitHubAppReadResult<Tamma.Platforms.GitHub.GitHubAppInstallationDetails>.NotConfigured());
        _gitHubApp
            .Setup(c => c.ListInstallationReposAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Tamma.Platforms.GitHub.GitHubAppReadResult<IReadOnlyList<Tamma.Platforms.GitHub.GitHubAppInstallationRepo>>.NotConfigured());
        _provisioner
            .Setup(p => p.PushAsync(
                It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<(string, string)>>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SecretProvisionResult>)Array.Empty<SecretProvisionResult>());
        _apiKeyRepo
            .Setup(r => r.ListByOwnerAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<ApiKey>());
        _apiKeyRepo
            .Setup(r => r.CreateAsync(It.IsAny<ApiKey>()))
            .ReturnsAsync((ApiKey k) => { k.Id = Guid.NewGuid(); return k; });
        _installRepo
            .Setup(r => r.ListReposAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<GitHubInstallationRepo>());

        _service = new InstallationRouterService(
            _installRepo.Object,
            _eventRepo.Object,
            _tenantRepo.Object,
            _userRepo.Object,
            new MemoryCache(new MemoryCacheOptions()),
            _gitHubApp.Object,
            _provisioner.Object,
            _apiKeyRepo.Object,
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
    public async Task HandleCallbackAsync_BridgesTheInstallation_BeforePushingSecrets()
    {
        // Epic 31 review (F-high) — DriverInstallationSecretsPusher resolves
        // the GitHub driver from the tenant_platform_installations row the
        // BRIDGE creates. The old push-then-bridge order meant a first-time
        // App install resolved no driver and provisioned ZERO repo secrets
        // (github_client_not_configured for every repo, nothing retried).
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        _userRepo.Setup(r => r.GetByIdAsync(userId))
            .ReturnsAsync(new User { Id = userId, Email = "test@example.com", TenantId = tenantId });
        _tenantRepo.Setup(r => r.GetByIdAsync(tenantId))
            .ReturnsAsync(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });
        _installRepo.Setup(r => r.GetByInstallationIdAsync(12345L))
            .ReturnsAsync((GitHubInstallation?)null);
        _installRepo.Setup(r => r.CreateAsync(It.IsAny<GitHubInstallation>()))
            .ReturnsAsync((GitHubInstallation i) => i);

        var order = new List<string>();
        var bridge = new Mock<Tamma.Api.Services.Platforms.IGitHubInstallationBridge>();
        bridge
            .Setup(b => b.EnsureBridgedAsync(tenantId, 12345L, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("bridge"))
            .ReturnsAsync(true);
        _provisioner
            .Setup(p => p.PushAsync(
                It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<(string, string)>>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("push"))
            .ReturnsAsync((IReadOnlyList<SecretProvisionResult>)Array.Empty<SecretProvisionResult>());

        var service = new InstallationRouterService(
            _installRepo.Object,
            _eventRepo.Object,
            _tenantRepo.Object,
            _userRepo.Object,
            new MemoryCache(new MemoryCacheOptions()),
            _gitHubApp.Object,
            _provisioner.Object,
            _apiKeyRepo.Object,
            _logger.Object,
            webhookSignals: null,
            installationBridge: bridge.Object);

        var result = await service.HandleCallbackAsync(12345L, null, userId);

        result.Success.Should().BeTrue();
        // The secrets pusher can only resolve a driver AFTER the bridge has
        // minted the tenant_platform_installations row.
        order.Should().Equal("bridge", "push");
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

    // ─── HandleWebhookAsync: workflow_run (story 19-3 AC-7) ───────────────────

    /// <summary>
    /// Build a router with a real <see cref="WebhookSignalRegistry"/> wired.
    /// Captures the registry so the test can inspect / register waiters.
    /// </summary>
    private (InstallationRouterService Svc, Tamma.Activities.AgentDispatch.WebhookSignalRegistry Registry) BuildServiceWithSignals()
    {
        var registry = new Tamma.Activities.AgentDispatch.WebhookSignalRegistry();
        var svc = new InstallationRouterService(
            _installRepo.Object,
            _eventRepo.Object,
            _tenantRepo.Object,
            _userRepo.Object,
            new MemoryCache(new MemoryCacheOptions()),
            _gitHubApp.Object,
            _provisioner.Object,
            _apiKeyRepo.Object,
            _logger.Object,
            webhookSignals: registry);
        return (svc, registry);
    }

    [Test]
    public async Task HandleWebhookAsync_WorkflowRunCompleted_MatchesPendingWaiter()
    {
        var (svc, registry) = BuildServiceWithSignals();

        var payload = JsonDocument.Parse("""
            {
              "action": "completed",
              "workflow_run": {
                "id": 77777777,
                "status": "completed",
                "conclusion": "success",
                "html_url": "https://github.com/acme/widgets/actions/runs/77777777",
                "artifacts_url": "https://api.github.com/repos/acme/widgets/actions/runs/77777777/artifacts",
                "head_branch": "tamma/issue-42",
                "created_at": "2026-04-18T10:00:00Z",
                "updated_at": "2026-04-18T10:05:00Z"
              },
              "repository": { "full_name": "acme/widgets" }
            }
            """).RootElement;

        // Park a waiter that expects a branch-fallback match.
        var waitKey = new Tamma.Activities.AgentDispatch.AgentWebhookSignalKey(
            Repository: "acme/widgets",
            HeadBranch: "tamma/issue-42",
            SessionId: "sess_abc",
            WorkflowRunId: null);
        var waitTask = registry.WaitForSignalAsync(waitKey, TimeSpan.FromSeconds(10));

        // Let the wait register before the webhook publishes.
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (registry.PendingWaiterCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        var result = await svc.HandleWebhookAsync("workflow_run", payload);

        result.Skipped.Should().BeFalse("a matching Tamma waiter was parked");
        result.EventType.Should().Be("workflow_run");
        result.Action.Should().Be("completed");

        var signal = await waitTask;
        signal.Should().NotBeNull();
        signal!.WorkflowRunId.Should().Be(77777777);
        signal.Conclusion.Should().Be("success");
        signal.Status.Should().Be("completed");
        signal.WorkflowRunUrl.Should().Be("https://github.com/acme/widgets/actions/runs/77777777");
    }

    [Test]
    public async Task HandleWebhookAsync_WorkflowRunCompleted_NoWaiter_ReturnsSkipped()
    {
        var (svc, registry) = BuildServiceWithSignals();

        var payload = JsonDocument.Parse("""
            {
              "action": "completed",
              "workflow_run": {
                "id": 42,
                "status": "completed",
                "conclusion": "failure",
                "html_url": "https://github.com/acme/widgets/actions/runs/42",
                "artifacts_url": "https://api.github.com/repos/acme/widgets/actions/runs/42/artifacts",
                "head_branch": "main",
                "created_at": "2026-04-18T10:00:00Z",
                "updated_at": "2026-04-18T10:01:00Z"
              },
              "repository": { "full_name": "acme/widgets" }
            }
            """).RootElement;

        var result = await svc.HandleWebhookAsync("workflow_run", payload);

        result.Skipped.Should().BeTrue(
            "an unmatched workflow_run is almost always a non-Tamma-dispatched run");
        registry.PendingWaiterCount.Should().Be(0);
    }

    [Test]
    public async Task HandleWebhookAsync_WorkflowRun_NonCompletedAction_IsSkipped()
    {
        var (svc, _) = BuildServiceWithSignals();

        var payload = JsonDocument.Parse("""
            {
              "action": "in_progress",
              "workflow_run": { "id": 1, "head_branch": "main" },
              "repository": { "full_name": "acme/widgets" }
            }
            """).RootElement;

        var result = await svc.HandleWebhookAsync("workflow_run", payload);

        result.Skipped.Should().BeTrue(
            "only terminal workflow_run.completed is relevant to the monitor");
    }

    [Test]
    public async Task HandleWebhookAsync_WorkflowRun_NoRegistry_IsSkipped()
    {
        // Default _service uses the base setup with no registry.
        var payload = JsonDocument.Parse("""
            {
              "action": "completed",
              "workflow_run": { "id": 1, "head_branch": "main" },
              "repository": { "full_name": "acme/widgets" }
            }
            """).RootElement;

        var result = await _service.HandleWebhookAsync("workflow_run", payload);

        result.Skipped.Should().BeTrue(
            "self-hosted deployments without the registry fall through");
    }

    [Test]
    public async Task HandleWebhookAsync_WorkflowRun_MissingWorkflowRun_IsSkipped()
    {
        var (svc, _) = BuildServiceWithSignals();

        var payload = JsonDocument.Parse("""
            {
              "action": "completed",
              "repository": { "full_name": "acme/widgets" }
            }
            """).RootElement;

        var result = await svc.HandleWebhookAsync("workflow_run", payload);

        result.Skipped.Should().BeTrue();
    }
}
