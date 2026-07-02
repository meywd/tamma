using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Git;
using Tamma.Core.Interfaces;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Git;

/// <summary>
/// Story 38 (Phase 1) — the GitHub "extra ops" (<c>GetCommits</c> /
/// <c>GetFileChanges</c> / <c>DeleteBranch</c>) added to <see cref="GitMediationService"/>.
/// Reuse the exact guard → token → platform-with-resolved-token → one-event plane as
/// the git-platform ops; assertions cover the fail-closed guard (deny ⇒ no token
/// resolved, platform never called), the happy paths + their mapped projections, and
/// the 503 token-unavailable path.
/// </summary>
[TestFixture]
public class GitExtraOpsMediationTests
{
    private const string SecretToken = "ghp-EXTRA-SECRET-DO-NOT-LEAK-1234567890";
    private const string Repo = "acme/widgets";

    private Mock<IGitRepoAuthorizer> _authorizer = null!;
    private Mock<IGitTokenResolver> _tokenResolver = null!;
    private Mock<IGitHubClientFactory> _factory = null!;
    private Mock<IGitHubIntegrationService> _github = null!;
    private RecordingRepo _events = null!;
    private GitMediationService _sut = null!;
    private readonly Guid _tenant = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _authorizer = new Mock<IGitRepoAuthorizer>(MockBehavior.Strict);
        _tokenResolver = new Mock<IGitTokenResolver>(MockBehavior.Strict);
        _factory = new Mock<IGitHubClientFactory>(MockBehavior.Strict);
        _github = new Mock<IGitHubIntegrationService>(MockBehavior.Loose);
        _events = new RecordingRepo();
        _factory.Setup(f => f.Create(It.IsAny<string>())).Returns(_github.Object);
        _sut = new GitMediationService(
            _authorizer.Object, _tokenResolver.Object, _factory.Object, _events, NullLogger<GitMediationService>.Instance);
    }

    private void Allow() => _authorizer
        .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(GitRepoAuthorization.Allow());

    private void Deny() => _authorizer
        .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(GitRepoAuthorization.Deny("not authorized"));

    private void ResolveToken() => _tokenResolver
        .Setup(t => t.ResolveAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new GitTokenResolution(SecretToken, GitCredentialSources.Byok));

    private void NoToken() => _tokenResolver
        .Setup(t => t.ResolveAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((GitTokenResolution?)null);

    [Test]
    public async Task GetCommits_Success_MapsCommits_OneEvent()
    {
        Allow();
        ResolveToken();
        _github.Setup(g => g.GetGitHubCommitsAsync(Repo, "main", null))
            .ReturnsAsync(IntegrationResult<List<GitHubCommit>>.Ok(new List<GitHubCommit>
            {
                new() { Sha = "abc", Message = "fix", Author = "bob", Additions = 3, Deletions = 1, Files = new List<string> { "a.cs" } },
            }));

        var result = await _sut.GetCommitsAsync(_tenant, Repo, "main", null, "corr-c");

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Done");
        result.Commits.Should().HaveCount(1);
        result.Commits![0].Sha.Should().Be("abc");
        result.Commits[0].Files.Should().Contain("a.cs");
        _factory.Verify(f => f.Create(SecretToken), Times.Once);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.CommitsReadSuccess);
    }

    [Test]
    public async Task GetCommits_GuardDenied_403_PlatformNeverCalled()
    {
        Deny();

        var result = await _sut.GetCommitsAsync(_tenant, Repo, "main", null, "corr-c");

        result.FailureCode.Should().Be(GitFailureCodes.RepoNotAuthorized);
        _tokenResolver.Verify(t => t.ResolveAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _factory.Verify(f => f.Create(It.IsAny<string>()), Times.Never);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.CommitsReadFailed);
    }

    [Test]
    public async Task GetFileChanges_Success_MapsChanges_OneEvent()
    {
        Allow();
        ResolveToken();
        _github.Setup(g => g.GetGitHubFileChangesAsync(Repo, "feature"))
            .ReturnsAsync(IntegrationResult<List<GitHubFileChange>>.Ok(new List<GitHubFileChange>
            {
                new() { FilePath = "a.cs", ChangeType = "modified", Additions = 2, Deletions = 0 },
            }));

        var result = await _sut.GetFileChangesAsync(_tenant, Repo, "feature", "corr-f");

        result.Success.Should().BeTrue();
        result.FileChanges.Should().HaveCount(1);
        result.FileChanges![0].FilePath.Should().Be("a.cs");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.FileChangesReadSuccess);
    }

    [Test]
    public async Task DeleteBranch_Success_OneEvent()
    {
        Allow();
        ResolveToken();
        _github.Setup(g => g.DeleteGitHubBranchAsync(Repo, "feature/foo"))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));

        var result = await _sut.DeleteBranchAsync(_tenant, Repo, "feature/foo", "corr-d");

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Deleted");
        result.BranchDeleted.Should().Be(true);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.BranchDeletedSuccess);
    }

    [Test]
    public async Task DeleteBranch_PlatformFailure_TypedError_OneFailedEvent()
    {
        Allow();
        ResolveToken();
        _github.Setup(g => g.DeleteGitHubBranchAsync(Repo, "feature"))
            .ReturnsAsync(IntegrationResult<bool>.Fail("404: not found"));

        var result = await _sut.DeleteBranchAsync(_tenant, Repo, "feature", "corr-d");

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.NotFound);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.BranchDeletedFailed);
    }

    [Test]
    public async Task GetCommits_TokenUnavailable_503_FailClosed()
    {
        Allow();
        NoToken();

        var result = await _sut.GetCommitsAsync(_tenant, Repo, "main", null, "corr-c");

        result.FailureCode.Should().Be(GitFailureCodes.TokenUnavailable);
        _factory.Verify(f => f.Create(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task CredentialSafety_TokenNeverLeaks()
    {
        Allow();
        ResolveToken();
        _github.Setup(g => g.GetGitHubFileChangesAsync(Repo, "main"))
            .ReturnsAsync(IntegrationResult<List<GitHubFileChange>>.Ok(new List<GitHubFileChange>()));

        var result = await _sut.GetFileChangesAsync(_tenant, Repo, "main", "corr-f");

        JsonSerializer.Serialize(result).Should().NotContain(SecretToken);
        foreach (var evt in _events.Appended)
            (evt.Tags + evt.Data + evt.Metadata).Should().NotContain(SecretToken);
    }

    private sealed class RecordingRepo : IEventRepository
    {
        public ConcurrentBag<DomainEvent> Appended { get; } = new();
        public Task<DomainEvent> AppendAsync(DomainEvent evt) { Appended.Add(evt); return Task.FromResult(evt); }
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }
}
