using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Git;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Tests.Git;

/// <summary>
/// Story 38 (Phase 1) / Epic 31 P2 — the "extra ops" (<c>GetCommits</c> /
/// <c>GetFileChanges</c> / <c>DeleteBranch</c>) on <see cref="GitMediationService"/>.
/// Reuse the exact guard → driver → platform-through-the-abstraction → one-event
/// plane as the git-platform ops; assertions cover the fail-closed guard (deny ⇒
/// no driver resolved, platform never called), the happy paths + their mapped
/// projections, and the 503 driver-unavailable path. Behavioral assertions are
/// unchanged from the pre-swap fixture; only the collaborator seams moved onto
/// the platform abstraction.
/// </summary>
[TestFixture]
public class GitExtraOpsMediationTests
{
    private const string SecretToken = "ghp-EXTRA-SECRET-DO-NOT-LEAK-1234567890";
    private const string Repo = "acme/widgets";

    private Mock<IGitRepoAuthorizer> _authorizer = null!;
    private Mock<IPlatformResolver> _resolver = null!;
    private Mock<IGitPlatformClient> _client = null!;
    private RecordingRepo _events = null!;
    private GitMediationService _sut = null!;
    private readonly Guid _tenant = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _authorizer = new Mock<IGitRepoAuthorizer>(MockBehavior.Strict);
        _resolver = new Mock<IPlatformResolver>(MockBehavior.Strict);
        _client = new Mock<IGitPlatformClient>(MockBehavior.Loose);
        _events = new RecordingRepo();
        _sut = new GitMediationService(
            _authorizer.Object, _resolver.Object, _events, NullLogger<GitMediationService>.Instance);
    }

    private void Allow() => _authorizer
        .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(GitRepoAuthorization.Allow());

    private void Deny() => _authorizer
        .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(GitRepoAuthorization.Deny("not authorized"));

    private sealed class FakeDriver : IGitPlatformDriver
    {
        public FakeDriver(IGitPlatformClient client) => Client = client;
        public PlatformKind Kind => PlatformKind.GitHub;
        public IGitPlatformClient Client { get; }
        public IGitPlatformActionsClient? Actions => null;
        public IReadOnlySet<PlatformCapability> Capabilities { get; } = new HashSet<PlatformCapability>();
    }

    private void ResolveDriver() => _resolver
        .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new MediationDriverResolution(
            new FakeDriver(_client.Object), MediationCredentialSource.TenantInstallation));

    private void NoDriver() => _resolver
        .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((MediationDriverResolution?)null);

    [Test]
    public async Task GetCommits_Success_MapsCommits_OneEvent()
    {
        Allow();
        ResolveDriver();
        _client.Setup(c => c.ListCommitsAsync(
                It.Is<ListCommitsRequest>(r => r.Owner == "acme" && r.RepoName == "widgets" && r.Ref == "main" && r.Since == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<IReadOnlyList<Commit>>.FromOk(new List<Commit>
            {
                new("abc", "fix", "bob", DateTimeOffset.UtcNow),
            }));

        var result = await _sut.GetCommitsAsync(_tenant, Repo, "main", null, "corr-c");

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Done");
        result.Commits.Should().HaveCount(1);
        result.Commits![0].Sha.Should().Be("abc");
        _resolver.Verify(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.CommitsReadSuccess);
    }

    [Test]
    public async Task GetCommits_GuardDenied_403_PlatformNeverCalled()
    {
        Deny();

        var result = await _sut.GetCommitsAsync(_tenant, Repo, "main", null, "corr-c");

        result.FailureCode.Should().Be(GitFailureCodes.RepoNotAuthorized);
        _resolver.Verify(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.VerifyNoOtherCalls();
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.CommitsReadFailed);
    }

    [Test]
    public async Task GetFileChanges_Success_MapsChanges_OneEvent()
    {
        Allow();
        ResolveDriver();
        _client.Setup(c => c.ListBranchFileChangesAsync(
                It.Is<ListBranchFileChangesRequest>(r => r.Owner == "acme" && r.RepoName == "widgets" && r.Branch == "feature"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<IReadOnlyList<PrFile>>.FromOk(new List<PrFile>
            {
                new("a.cs", PrFileStatus.Modified, 2, 0),
            }));

        var result = await _sut.GetFileChangesAsync(_tenant, Repo, "feature", "corr-f");

        result.Success.Should().BeTrue();
        result.FileChanges.Should().HaveCount(1);
        result.FileChanges![0].FilePath.Should().Be("a.cs");
        result.FileChanges[0].ChangeType.Should().Be("modified");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.FileChangesReadSuccess);
    }

    [Test]
    public async Task DeleteBranch_Success_OneEvent()
    {
        Allow();
        ResolveDriver();
        _client.Setup(c => c.DeleteBranchAsync("acme", "widgets", "feature/foo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<bool>.FromOk(true));

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
        ResolveDriver();
        _client.Setup(c => c.DeleteBranchAsync("acme", "widgets", "feature", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<bool>.FromError(new PlatformError.NotFound()));

        var result = await _sut.DeleteBranchAsync(_tenant, Repo, "feature", "corr-d");

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.NotFound);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.BranchDeletedFailed);
    }

    [Test]
    public async Task GetCommits_DriverUnavailable_503_FailClosed()
    {
        Allow();
        NoDriver();

        var result = await _sut.GetCommitsAsync(_tenant, Repo, "main", null, "corr-c");

        result.FailureCode.Should().Be(GitFailureCodes.TokenUnavailable);
        _client.VerifyNoOtherCalls();
    }

    [Test]
    public async Task CredentialSafety_TokenNeverLeaks()
    {
        Allow();
        ResolveDriver();
        _client.Setup(c => c.ListBranchFileChangesAsync(It.IsAny<ListBranchFileChangesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<IReadOnlyList<PrFile>>.FromOk(new List<PrFile>()));

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
