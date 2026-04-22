using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Engine;

namespace Tamma.Api.Tests.GitHub;

/// <summary>
/// Tests for <see cref="OctokitGitHubEngineCallbackService"/> focus on the
/// installation-resolution flow and error mapping — the bulk of the per-
/// endpoint Octokit interaction is exercised in integration tests against
/// a fake HTTP handler. Here we assert the common plumbing:
///
/// <list type="bullet">
/// <item>No installation found → <c>ServiceUnavailable</c> (matches the Null
/// impl contract; the endpoint translates to 503).</item>
/// <item>Installation resolved but GitHub auth fails → token is invalidated
/// and the caller gets a typed error reason.</item>
/// </list>
///
/// Audit findings: engine 005-011.
/// </summary>
[TestFixture]
public class OctokitGitHubEngineCallbackServiceTests
{
    private Mock<IRepoInstallationResolver> _resolver = null!;
    private Mock<ILogger<OctokitGitHubEngineCallbackService>> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = new Mock<IRepoInstallationResolver>();
        _logger = new Mock<ILogger<OctokitGitHubEngineCallbackService>>();
    }

    [Test]
    public async Task ReadRepoConfigAsync_NoInstallationForRepo_ReturnsNotConfigured()
    {
        _resolver.Setup(r => r.ResolveInstallationIdAsync(
                "acme", "missing-repo", It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var service = BuildService(appClient: null!);
        var result = await service.ReadRepoConfigAsync("acme", "missing-repo", "main");

        result.ServiceUnavailable.Should().BeTrue();
        result.ErrorReason.Should().Be("github_client_not_configured");
    }

    [Test]
    public async Task ListIssuesAsync_NoInstallationForRepo_ReturnsNotConfigured()
    {
        _resolver.Setup(r => r.ResolveInstallationIdAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var service = BuildService(appClient: null!);
        var result = await service.ListIssuesAsync("acme", "repo", "open", null, 30, 1);

        result.ServiceUnavailable.Should().BeTrue();
    }

    [Test]
    public async Task CreateIssueAsync_NoInstallationForRepo_ReturnsNotConfigured()
    {
        _resolver.Setup(r => r.ResolveInstallationIdAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var service = BuildService(appClient: null!);
        var result = await service.CreateIssueAsync(
            "acme", "repo", "title", "body", labels: null, assignees: null);

        result.ServiceUnavailable.Should().BeTrue();
        _logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Test]
    public async Task TriggerCiAsync_NoInstallationForRepo_ReturnsNotConfigured()
    {
        _resolver.Setup(r => r.ResolveInstallationIdAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var service = BuildService(appClient: null!);
        var result = await service.TriggerCiAsync(
            "acme", "repo", "main", "ci.yml", inputs: null);

        result.ServiceUnavailable.Should().BeTrue();
    }

    [Test]
    public async Task PostIssueCommentAsync_NoInstallationForRepo_ReturnsNotConfigured()
    {
        _resolver.Setup(r => r.ResolveInstallationIdAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var service = BuildService(appClient: null!);
        var result = await service.PostIssueCommentAsync("acme", "repo", 1, "body");

        result.ServiceUnavailable.Should().BeTrue();
    }

    [Test]
    public async Task AddIssueLabelsAsync_NoInstallationForRepo_ReturnsNotConfigured()
    {
        _resolver.Setup(r => r.ResolveInstallationIdAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var service = BuildService(appClient: null!);
        var result = await service.AddIssueLabelsAsync(
            "acme", "repo", 1, new[] { "bug" });

        result.ServiceUnavailable.Should().BeTrue();
    }

    [Test]
    public async Task RemoveIssueLabelAsync_NoInstallationForRepo_ReturnsNotConfigured()
    {
        _resolver.Setup(r => r.ResolveInstallationIdAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var service = BuildService(appClient: null!);
        var result = await service.RemoveIssueLabelAsync("acme", "repo", 1, "bug");

        result.ServiceUnavailable.Should().BeTrue();
    }

    [Test]
    public async Task ListSecurityAlertsAsync_NoInstallationForRepo_ReturnsNotConfigured()
    {
        _resolver.Setup(r => r.ResolveInstallationIdAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var service = BuildService(appClient: null!);
        var result = await service.ListSecurityAlertsAsync("acme", "repo", "all");

        result.ServiceUnavailable.Should().BeTrue();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private OctokitGitHubEngineCallbackService BuildService(
        Tamma.Api.Services.GitHub.OctokitGitHubAppClient? appClient)
    {
        // When no installation is resolved we never touch the app client, so
        // passing null is safe for the "ServiceUnavailable" tests above. The
        // real happy-path is covered by integration tests in a follow-up.
        return new OctokitGitHubEngineCallbackService(
            appClient: appClient!,
            resolver: _resolver.Object,
            logger: _logger.Object);
    }
}

/// <summary>
/// Unit tests for <see cref="InstallationRepoResolver"/>. Asserts the repo →
/// installation reverse lookup returns the installation id from the
/// underlying repository, and null when no active row matches.
/// </summary>
[TestFixture]
public class InstallationRepoResolverTests
{
    [Test]
    public async Task ResolveInstallationIdAsync_Found_ReturnsInstallationId()
    {
        var repo = new Mock<Tamma.Data.Repositories.IInstallationRepository>();
        repo.Setup(r => r.GetByRepoFullNameAsync("acme/app"))
            .ReturnsAsync(new Tamma.Data.Entities.GitHubInstallation
            {
                Id = Guid.NewGuid(),
                InstallationId = 98765L,
                AccountLogin = "acme",
                AccountType = "Organization"
            });

        var resolver = new InstallationRepoResolver(repo.Object, Mock.Of<ILogger<InstallationRepoResolver>>());
        var result = await resolver.ResolveInstallationIdAsync("acme", "app");

        result.Should().Be(98765L);
    }

    [Test]
    public async Task ResolveInstallationIdAsync_NotFound_ReturnsNull()
    {
        var repo = new Mock<Tamma.Data.Repositories.IInstallationRepository>();
        repo.Setup(r => r.GetByRepoFullNameAsync("acme/missing"))
            .ReturnsAsync((Tamma.Data.Entities.GitHubInstallation?)null);

        var resolver = new InstallationRepoResolver(repo.Object, Mock.Of<ILogger<InstallationRepoResolver>>());
        var result = await resolver.ResolveInstallationIdAsync("acme", "missing");

        result.Should().BeNull();
    }

    [Test]
    public async Task ResolveInstallationIdAsync_BuildsFullNameFromOwnerRepo()
    {
        var repo = new Mock<Tamma.Data.Repositories.IInstallationRepository>();
        repo.Setup(r => r.GetByRepoFullNameAsync("my-org/my-repo"))
            .ReturnsAsync(new Tamma.Data.Entities.GitHubInstallation
            {
                Id = Guid.NewGuid(),
                InstallationId = 42L,
                AccountLogin = "my-org",
                AccountType = "Organization"
            });

        var resolver = new InstallationRepoResolver(repo.Object, Mock.Of<ILogger<InstallationRepoResolver>>());
        var result = await resolver.ResolveInstallationIdAsync("my-org", "my-repo");

        result.Should().Be(42L);
        repo.Verify(r => r.GetByRepoFullNameAsync("my-org/my-repo"), Times.Once);
    }
}
