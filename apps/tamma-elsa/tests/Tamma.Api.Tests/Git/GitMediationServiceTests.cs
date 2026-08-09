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
/// Story 38-1 / Epic 31 P2 — <see cref="GitMediationService"/> composition
/// (guard → driver resolution → platform-through-the-abstraction → one DCB
/// event). Every collaborator is faked; assertions cover the fail-closed order
/// (guard FIRST — deny ⇒ no driver resolved, platform never invoked), BYOK vs
/// platform <c>credentialSource</c>, the typed key-free failure taxonomy with a
/// preserved <c>platformStatusCode</c>, the 503 token-unavailable path, the
/// exactly-one-terminal-event invariant + its tags, and credential safety (no
/// credential appears in any result / event — post-swap, the credential never
/// even ENTERS the mediation layer; it lives inside the resolved driver).
///
/// <para><b>P2 note.</b> The pre-swap fixture mocked the deleted
/// IGitHubClientFactory + the GitHub-specific integration client. The SETUP
/// moved onto <see cref="IPlatformResolver"/> + <see cref="IGitPlatformClient"/>;
/// every BEHAVIORAL assertion (status codes, failure codes, outcomes, event
/// types, tag shapes) is unchanged from the pre-swap fixture — that is the
/// parity claim this file pins.</para>
/// </summary>
[TestFixture]
public class GitMediationServiceTests
{
    private const string SecretToken = "ghp-SUPER-SECRET-DO-NOT-LEAK-1234567890";
    private const string Repo = "acme/widgets";

    private Mock<IGitRepoAuthorizer> _authorizer = null!;
    private Mock<IPlatformResolver> _resolver = null!;
    private Mock<IGitPlatformClient> _client = null!;
    private RecordingEventRepository _events = null!;
    private GitMediationService _sut = null!;
    private readonly Guid _tenant = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _authorizer = new Mock<IGitRepoAuthorizer>(MockBehavior.Strict);
        _resolver = new Mock<IPlatformResolver>(MockBehavior.Strict);
        _client = new Mock<IGitPlatformClient>(MockBehavior.Loose);
        _events = new RecordingEventRepository();

        _sut = new GitMediationService(
            _authorizer.Object, _resolver.Object, _events,
            NullLogger<GitMediationService>.Instance);
    }

    private void Allow() => _authorizer
        .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(GitRepoAuthorization.Allow());

    private void Deny() => _authorizer
        .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(GitRepoAuthorization.Deny("not authorized"));

    private sealed class FakeDriver : IGitPlatformDriver
    {
        public FakeDriver(IGitPlatformClient client, IReadOnlySet<PlatformCapability>? capabilities = null)
        {
            Client = client;
            Capabilities = capabilities ?? new HashSet<PlatformCapability>
            {
                PlatformCapability.PrLifecycle,
                // P5 M2 (DG-2): the mediation review-comment core consults the
                // resolved driver's capabilities before attempting the anchored
                // post — the default fixture driver is anchoring-capable so the
                // pre-M2 behavioral pins run unchanged.
                PlatformCapability.PrFileReview,
            };
        }

        public PlatformKind Kind => PlatformKind.GitHub;
        public IGitPlatformClient Client { get; }
        public IGitPlatformActionsClient? Actions => null;
        public IReadOnlySet<PlatformCapability> Capabilities { get; }
    }

    private void ResolveDriver(string source, IReadOnlySet<PlatformCapability>? capabilities = null) => _resolver
        .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new MediationDriverResolution(
            new FakeDriver(_client.Object, capabilities),
            source == GitCredentialSources.Byok
                ? MediationCredentialSource.TenantInstallation
                : MediationCredentialSource.PlatformDefault));

    private void NoDriver() => _resolver
        .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((MediationDriverResolution?)null);

    // ── platform model helpers ──

    private static PullRequest Pr(
        string number = "15",
        PullRequestState state = PullRequestState.Open,
        bool isDraft = false,
        bool? mergeable = null,
        string? mergeSha = null,
        string url = "https://gh/pr/x",
        string source = "feature",
        string target = "main") =>
        new(number, "t", null, source, target, state, isDraft, url, "bot",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        { Mergeable = mergeable, MergeCommitSha = mergeSha };

    private static PlatformResult<T> Ok<T>(T value) => PlatformResult<T>.FromOk(value);

    private static PlatformResult<T> Fail<T>(PlatformError error) => PlatformResult<T>.FromError(error);

    private static CreatePrRequest PrBody() => new()
    {
        Title = "[ADL] #7: thing", Body = "body", HeadRef = "feature", BaseRef = "main", CorrelationId = "corr-pr",
    };

    private static MergePrRequest MergeBody() => new()
    {
        MergeStrategy = "squash", IssueNumber = 7, BranchName = "feature", CorrelationId = "corr-merge",
    };

    private static Tamma.Api.Services.Git.CreateReleaseRequest ReleaseBody() => new()
    {
        TagName = "deploy-abc1234", TargetRef = "abc1234def", Name = "Release deploy-abc1234",
        Body = "notes", IssueNumber = 7, CorrelationId = "corr-release",
    };

    private void NoExistingOpenPr() => _client
        .Setup(c => c.ListOpenPullRequestsForBranchAsync("acme", "widgets", "feature", "main", It.IsAny<CancellationToken>()))
        .ReturnsAsync(Ok<IReadOnlyList<PullRequest>>(Array.Empty<PullRequest>()));

    // ===================================================================
    // Cross-tenant guard (AC2) — FIRST, fail-closed
    // ===================================================================

    [Test]
    public async Task CreatePr_GuardDenied_403_NoDriverResolved_PlatformNeverCalled()
    {
        Deny();

        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, PrBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.RepoNotAuthorized);
        result.CredentialSource.Should().BeNull("no driver is resolved on a guard denial");

        _resolver.Verify(
            r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never, "the guard runs BEFORE driver resolution");
        _client.VerifyNoOtherCalls();

        _events.Appended.Should().HaveCount(1);
        var evt = _events.Appended.Single();
        evt.Type.Should().Be(GitEventTypes.PrOpenedFailed);
        evt.Tags.Should().Contain(GitFailureCodes.RepoNotAuthorized);
    }

    [Test]
    public void GuardDenied_ToHttpResult_Is403()
    {
        var denied = new GitMediationResult { Success = false, FailureCode = GitFailureCodes.RepoNotAuthorized };
        var http = denied.ToHttpResult();
        StatusOf(http).Should().Be(403);
    }

    // ===================================================================
    // Driver resolution (AC3) — BYOK / platform / fail-closed
    // ===================================================================

    [Test]
    public async Task CreatePr_Success_Byok_ResolvesDriverOnce_EmitsOneSuccessEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        NoExistingOpenPr();
        _client.Setup(c => c.OpenPullRequestAsync(It.IsAny<OpenPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr(number: "42", url: "https://gh/pr/42")));

        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, PrBody());

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Created");
        result.PrNumber.Should().Be(42);
        result.PrUrl.Should().Be("https://gh/pr/42");
        result.CredentialSource.Should().Be(GitCredentialSources.Byok);

        // The P2 invariant: the driver used == the driver resolved, exactly once.
        _resolver.Verify(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);

        _events.Appended.Should().HaveCount(1);
        var evt = _events.Appended.Single();
        evt.Type.Should().Be(GitEventTypes.PrOpenedSuccess);
        evt.TenantId.Should().Be(_tenant);
        AssertTags(evt, operation: GitEventTypes.PrOpenOperation, credentialSource: GitCredentialSources.Byok, correlationId: "corr-pr");
    }

    [Test]
    public async Task CreatePr_Success_Platform_StampsPlatformCredentialSource()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Platform);
        NoExistingOpenPr();
        _client.Setup(c => c.OpenPullRequestAsync(It.IsAny<OpenPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr(number: "9", url: "u")));

        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, PrBody());

        result.CredentialSource.Should().Be(GitCredentialSources.Platform);
        _resolver.Verify(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task CreatePr_DriverUnavailable_503_FailClosed_PlatformNeverCalled()
    {
        Allow();
        NoDriver();

        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, PrBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.TokenUnavailable);
        _client.VerifyNoOtherCalls();

        result.ToHttpResult().Let(StatusOf).Should().Be(503);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrOpenedFailed);
    }

    // ===================================================================
    // Typed platform failures (AC6) — key-free + preserved platformStatusCode
    // ===================================================================

    [Test]
    public async Task CreatePr_PlatformConflict_200SuccessFalse_GitConflict()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Platform);
        NoExistingOpenPr();
        _client.Setup(c => c.OpenPullRequestAsync(It.IsAny<OpenPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<PullRequest>(new PlatformError.InvalidRequest("merge_conflict", "a merge conflict")));

        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, PrBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.GitConflict);
        result.ToHttpResult().Let(StatusOf).Should().Be(200, "expected platform failures ride inside 200 success:false");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrOpenedFailed);
    }

    [Test]
    public async Task Merge_ClosedUnmerged_NotMergeable()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.GetPullRequestAsync("acme", "widgets", "15", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr(state: PullRequestState.Closed)));

        var result = await _sut.MergePullRequestAsync(_tenant, Repo, 15, MergeBody());

        result.Success.Should().BeFalse();
        result.Merged.Should().Be(false);
        result.FailureCode.Should().Be(GitFailureCodes.NotMergeable);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrMergeFailed);
    }

    [Test]
    public async Task Merge_ConflictOnMerge_GitConflict_PreservesPlatformStatusCode()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.GetPullRequestAsync("acme", "widgets", "15", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr(state: PullRequestState.Open, mergeable: null)));
        _client.Setup(c => c.MergePullRequestAsync(It.IsAny<MergePullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<PullRequest>(new PlatformError.InvalidRequest("merge_conflict", "merge conflict")));

        var result = await _sut.MergePullRequestAsync(_tenant, Repo, 15, MergeBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.GitConflict);
        result.PlatformStatusCode.Should().Be(409, "the upstream status is preserved for the workflow to branch on");
    }

    // ===================================================================
    // Merge happy path + verified post-merge cleanup
    // ===================================================================

    [Test]
    public async Task Merge_Success_MergedWithIssueClosedAndBranchDeleted()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.GetPullRequestAsync("acme", "widgets", "15", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr(state: PullRequestState.Open, mergeable: true)));
        _client.Setup(c => c.MergePullRequestAsync(It.IsAny<MergePullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr(state: PullRequestState.Merged, mergeSha: "sha-123")));
        _client.Setup(c => c.CloseIssueAsync("acme", "widgets", "7", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new Issue("7", "t", null, IssueState.Closed, "u", Array.Empty<string>())));
        _client.Setup(c => c.DeleteBranchAsync("acme", "widgets", "feature", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(true));

        var result = await _sut.MergePullRequestAsync(_tenant, Repo, 15, MergeBody());

        result.Success.Should().BeTrue();
        result.Merged.Should().Be(true);
        result.MergeSha.Should().Be("sha-123");
        result.IssueClosed.Should().Be(true);
        result.BranchDeleted.Should().Be(true);
        result.Outcome.Should().Be("Merged");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrMergedSuccess);
    }

    // ===================================================================
    // Comments read + issue update
    // ===================================================================

    [Test]
    public async Task GetComments_Success_ReturnsMappedComments_OneEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Platform);
        _client.Setup(c => c.ListPullRequestReviewCommentsAsync("acme", "widgets", "3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok<IReadOnlyList<PullRequestReviewComment>>(new List<PullRequestReviewComment>
            {
                new("1", "this is a bug", "bob", DateTimeOffset.UtcNow, "a.cs", 5),
                new("2", "lgtm", "sue", DateTimeOffset.UtcNow, null, null),
            }));

        var result = await _sut.GetPullRequestCommentsAsync(_tenant, Repo, 3, "corr-c");

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Done");
        result.Comments.Should().HaveCount(2);
        result.Comments![0].Body.Should().Be("this is a bug");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrCommentsReadSuccess);
    }

    [Test]
    public async Task GetComments_GuardDenied_403_PlatformNeverCalled()
    {
        Deny();

        var result = await _sut.GetPullRequestCommentsAsync(_tenant, Repo, 3, "corr-c");

        result.FailureCode.Should().Be(GitFailureCodes.RepoNotAuthorized);
        _resolver.Verify(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.VerifyNoOtherCalls();
    }

    [Test]
    public async Task UpdateIssue_Success_PostsCommentAndLabels_OneEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.CreateIssueCommentAsync("acme", "widgets", "8", "status", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new IssueComment("1", "status", "bot", DateTimeOffset.UtcNow)));
        _client.Setup(c => c.AddIssueLabelsAsync(It.IsAny<AddIssueLabelsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok<IReadOnlyList<string>>(new[] { "in-progress" }));

        var body = new UpdateIssueRequest { Body = "status", AddLabels = new[] { "in-progress" }, CorrelationId = "corr-i" };
        var result = await _sut.UpdateIssueAsync(_tenant, Repo, 8, body);

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Updated");
        result.IssueStatus.Should().Be("updated");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.IssueUpdatedSuccess);
    }

    [Test]
    public async Task UpdateIssue_CommentFails_LoudFailure_PreservesStatus()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.CreateIssueCommentAsync("acme", "widgets", "8", "status", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<IssueComment>(new PlatformError.PermissionDenied()));

        var body = new UpdateIssueRequest { Body = "status", CorrelationId = "corr-i" };
        var result = await _sut.UpdateIssueAsync(_tenant, Repo, 8, body);

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be("Failed");
        result.PlatformStatusCode.Should().Be(403);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.IssueUpdatedFailed);
    }

    // ===================================================================
    // Create release (Epic 38 follow-up #21) — deployment-pipeline release step
    // ===================================================================

    [Test]
    public async Task CreateRelease_Success_Byok_EmitsOneSuccessEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.CreateReleaseAsync(It.IsAny<Tamma.Platforms.Abstractions.Models.CreateReleaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new Release("55", "deploy-abc1234", "Release deploy-abc1234", "https://gh/releases/55", false, false)));

        var result = await _sut.CreateReleaseAsync(_tenant, Repo, ReleaseBody());

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Created");
        result.ReleaseId.Should().Be(55);
        result.ReleaseUrl.Should().Be("https://gh/releases/55");
        result.ReleaseTag.Should().Be("deploy-abc1234");
        result.CredentialSource.Should().Be(GitCredentialSources.Byok);

        _resolver.Verify(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.ReleaseCreatedSuccess);
    }

    [Test]
    public async Task CreateRelease_PlatformError_200SuccessFalse_OneFailedEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Platform);
        _client.Setup(c => c.CreateReleaseAsync(It.IsAny<Tamma.Platforms.Abstractions.Models.CreateReleaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<Release>(new PlatformError.InvalidRequest("already_exists", "tag already exists")));

        var result = await _sut.CreateReleaseAsync(_tenant, Repo, ReleaseBody());

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be("Error");
        result.FailureCode.Should().Be(GitFailureCodes.PlatformError);
        result.ToHttpResult().Let(StatusOf).Should().Be(200, "an expected platform failure rides inside 200 success:false");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.ReleaseCreatedFailed);
    }

    [Test]
    public async Task CreateRelease_GuardDenied_403_PlatformNeverCalled()
    {
        Deny();

        var result = await _sut.CreateReleaseAsync(_tenant, Repo, ReleaseBody());

        result.FailureCode.Should().Be(GitFailureCodes.RepoNotAuthorized);
        _resolver.Verify(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.VerifyNoOtherCalls();
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.ReleaseCreatedFailed);
    }

    [Test]
    public async Task CreateRelease_DriverUnavailable_503_FailClosed_PlatformNeverCalled()
    {
        Allow();
        NoDriver();

        var result = await _sut.CreateReleaseAsync(_tenant, Repo, ReleaseBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.TokenUnavailable);
        _client.VerifyNoOtherCalls();
        result.ToHttpResult().Let(StatusOf).Should().Be(503);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.ReleaseCreatedFailed);
    }

    // ===================================================================
    // F3 — an unexpected exception (DB/decrypt/compose/transport) between the
    // guard and the platform call becomes a typed PLATFORM_ERROR (never a raw
    // 5xx) with exactly ONE terminal FAILED event.
    // ===================================================================

    [Test]
    public async Task GuardThrows_TypedPlatformError_OneFailedEvent_No5xx()
    {
        _authorizer
            .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("installation lookup DB down"));

        Func<Task> act = async () => await _sut.CreatePullRequestAsync(_tenant, Repo, PrBody());
        await act.Should().NotThrowAsync("a guard exception must never surface as a raw 5xx");

        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, PrBody());
        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.PlatformError);
        result.Outcome.Should().Be("Error");
        result.ToHttpResult().Let(StatusOf).Should().Be(200);

        // Two calls above, each fires exactly one FAILED event.
        _events.Appended.Should().OnlyContain(e => e.Type == GitEventTypes.PrOpenedFailed);
        _events.Appended.Should().HaveCount(2);
    }

    [Test]
    public async Task ResolverThrows_TypedPlatformError_OneFailedEvent()
    {
        Allow();
        _resolver
            .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("secret decrypt failed"));

        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, PrBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.PlatformError);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrOpenedFailed);
    }

    [Test]
    public async Task DriverComposeThrows_TypedPlatformError_OneFailedEvent()
    {
        Allow();
        _resolver
            .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("driver compose failed"));

        var result = await _sut.MergePullRequestAsync(_tenant, Repo, 15, MergeBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.PlatformError);
        result.ToHttpResult().Let(StatusOf).Should().Be(200);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrMergeFailed);
    }

    // ===================================================================
    // Credential safety (AC3/AC7) — no credential appears in any result / event.
    // Post-swap the credential never enters the mediation layer at all; this
    // canary pins that no plumbing regression re-introduces it.
    // ===================================================================

    [Test]
    public async Task CredentialSafety_NoCredentialAppearsInResultOrEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        NoExistingOpenPr();
        _client.Setup(c => c.OpenPullRequestAsync(It.IsAny<OpenPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr(number: "1", url: "u")));

        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, PrBody());

        JsonSerializer.Serialize(result).Should().NotContain(SecretToken);
        foreach (var evt in _events.Appended)
        {
            (evt.Tags + evt.Data + evt.Metadata).Should().NotContain(SecretToken);
        }
        result.CredentialSource.Should().Be(GitCredentialSources.Byok, "only the LABEL is surfaced");
    }

    // ===================================================================
    // Story 31-13 — the 7 PR-lifecycle verbs
    // ===================================================================

    [Test]
    public async Task ClosePr_Success_EmitsExactlyOnePrClosedSuccessEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.ClosePullRequestAsync("acme", "widgets", "15", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr(state: PullRequestState.Closed)));

        var result = await _sut.ClosePullRequestAsync(_tenant, Repo, 15, new ClosePrRequest { CorrelationId = "corr-close" });

        result.Success.Should().BeTrue();
        result.PrState.Should().Be("closed");
        result.Outcome.Should().Be("Closed");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrClosedSuccess);
    }

    [Test]
    public async Task ReopenPr_Success_EmitsExactlyOnePrReopenedSuccessEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.ReopenPullRequestAsync("acme", "widgets", "15", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr(state: PullRequestState.Open)));

        var result = await _sut.ReopenPullRequestAsync(_tenant, Repo, 15, new ReopenPrRequest { CorrelationId = "corr-reopen" });

        result.Success.Should().BeTrue();
        result.PrState.Should().Be("open");
        result.Outcome.Should().Be("Reopened");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrReopenedSuccess);
    }

    [Test]
    public async Task CommentPr_Success_EmitsExactlyOnePrCommentedSuccessEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.CreateIssueCommentAsync("acme", "widgets", "15", "hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new IssueComment("9", "hello", "bot", DateTimeOffset.UtcNow)));

        var result = await _sut.CommentOnPullRequestAsync(_tenant, Repo, 15, new PrCommentRequest { Body = "hello", CorrelationId = "corr-cmt" });

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Commented");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrCommentedSuccess);
    }

    [Test]
    public async Task ReviewCommentPr_Success_EmitsExactlyOnePrReviewCommentedSuccessEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.CreatePullRequestReviewCommentAsync(
                It.Is<CreatePullRequestReviewCommentRequest>(r =>
                    r.Owner == "acme" && r.RepoName == "widgets" && r.PrNumber == "15"
                    && r.Body == "nit" && r.CommitSha == "sha1" && r.Path == "a.cs" && r.Line == 5 && r.Side == "RIGHT"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new IssueComment("77", "nit", "bot", DateTimeOffset.UtcNow)));

        var body = new PrReviewCommentRequest { Body = "nit", CommitId = "sha1", Path = "a.cs", Line = 5, Side = "RIGHT", CorrelationId = "corr-rc" };
        var result = await _sut.ReviewCommentOnPullRequestAsync(_tenant, Repo, 15, body);

        result.Success.Should().BeTrue();
        result.CommentId.Should().Be(77);
        result.Outcome.Should().Be("Commented");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrReviewCommentedSuccess);
    }

    [Test]
    public async Task RequestReviewersPr_Success_EmitsExactlyOnePrReviewersRequestedSuccessEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.RequestReviewersAsync(It.IsAny<RequestReviewersRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr()));

        var body = new PrReviewersRequest { Reviewers = new[] { "alice", "bob" }, CorrelationId = "corr-rev" };
        var result = await _sut.RequestPullRequestReviewersAsync(_tenant, Repo, 15, body);

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("ReviewersRequested");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrReviewersRequestedSuccess);
    }

    [Test]
    public async Task LabelsPr_Success_AddAndRemove_EmitsExactlyOnePrLabelsUpdatedSuccessEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.AddPullRequestLabelsAsync(It.IsAny<AddPullRequestLabelsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr()));
        _client.Setup(c => c.RemovePullRequestLabelAsync("acme", "widgets", "15", "stale", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr()));

        var body = new PrLabelsRequest { AddLabels = new[] { "ready" }, RemoveLabels = new[] { "stale" }, CorrelationId = "corr-lbl" };
        var result = await _sut.UpdatePullRequestLabelsAsync(_tenant, Repo, 15, body);

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("LabelsUpdated");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrLabelsUpdatedSuccess);
    }

    [Test]
    public async Task DraftPr_Success_EmitsExactlyOnePrDraftSetSuccessEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.SetDraftAsync(It.IsAny<SetPullRequestDraftRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr(state: PullRequestState.Open, isDraft: true)));

        var result = await _sut.SetPullRequestDraftAsync(_tenant, Repo, 15, new PrDraftRequest { Draft = true, CorrelationId = "corr-draft" });

        result.Success.Should().BeTrue();
        result.IsDraft.Should().Be(true);
        result.Outcome.Should().Be("DraftSet");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrDraftSetSuccess);
    }

    // ── Epic 31 P2 (plan §4) — the typed capability refusal surfaces FIRST-CLASS
    //    (failureCode = capability_unsupported, exact code) so the workflow's
    //    Unsupported safety-net outcome can branch on it. ──

    [Test]
    public async Task DraftPr_CapabilityUnsupported_SurfacesFirstClassFailureCode()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.SetDraftAsync(It.IsAny<SetPullRequestDraftRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<PullRequest>(new PlatformError.InvalidRequest(
                "capability_unsupported", "platform does not support draft toggling")));

        var result = await _sut.SetPullRequestDraftAsync(_tenant, Repo, 15, new PrDraftRequest { Draft = false, CorrelationId = "corr-draft" });

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.CapabilityUnsupported,
            "the exact code must round-trip — never coarsened into PLATFORM_ERROR");
        result.FailureCode.Should().Be("capability_unsupported");
        result.ToHttpResult().Let(StatusOf).Should().Be(200, "an unsupported capability is an expected, branchable failure");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrDraftSetFailed);
        _events.Appended.Single().Tags.Should().Contain("capability_unsupported");
    }

    // ── Guard-deny: close AND draft (the GET-bearing verb) — REPO_NOT_AUTHORIZED,
    //    platform verb NEVER called, no driver resolved ──

    [Test]
    public async Task ClosePr_GuardDenied_NeverResolvesDriver_PlatformNeverCalled()
    {
        Deny();

        var result = await _sut.ClosePullRequestAsync(_tenant, Repo, 15, new ClosePrRequest { CorrelationId = "corr-close" });

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.RepoNotAuthorized);
        _resolver.Verify(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.Verify(c => c.ClosePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.VerifyNoOtherCalls();
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrClosedFailed);
    }

    [Test]
    public async Task DraftPr_GuardDenied_NeverResolvesDriver_PlatformNeverCalled()
    {
        Deny();

        var result = await _sut.SetPullRequestDraftAsync(_tenant, Repo, 15, new PrDraftRequest { Draft = true, CorrelationId = "corr-draft" });

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.RepoNotAuthorized);
        _resolver.Verify(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.Verify(c => c.SetDraftAsync(It.IsAny<SetPullRequestDraftRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.VerifyNoOtherCalls();
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrDraftSetFailed);
    }

    // ── Driver-unavailable: close AND review-comment — GIT_TOKEN_UNAVAILABLE
    //    fail-closed, exactly one FAILED event, platform verb never called ──

    [Test]
    public async Task ClosePr_DriverUnavailable_FailsClosed_PlatformNeverCalled()
    {
        Allow();
        NoDriver();

        var result = await _sut.ClosePullRequestAsync(_tenant, Repo, 15, new ClosePrRequest { CorrelationId = "corr-close" });

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.TokenUnavailable);
        _client.Verify(c => c.ClosePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrClosedFailed);
    }

    [Test]
    public async Task ReviewCommentPr_DriverUnavailable_FailsClosed_PlatformNeverCalled()
    {
        Allow();
        NoDriver();

        var body = new PrReviewCommentRequest { Body = "nit", Path = "a.cs", Line = 5, CorrelationId = "corr-rc" };
        var result = await _sut.ReviewCommentOnPullRequestAsync(_tenant, Repo, 15, body);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.TokenUnavailable);
        _client.Verify(c => c.CreatePullRequestReviewCommentAsync(
            It.IsAny<CreatePullRequestReviewCommentRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrReviewCommentedFailed);
    }

    // ===================================================================
    // Epic 31 P5 M2 — §4 degradation pairs at the mediation layer
    // (DG-2 review-comment downgrade, DG-3 reviewer skip, DG-4 merge-
    // method fallback). Every degraded trip is audited; nothing is
    // silently dropped; real failures are never mis-classified (§4.5).
    // ===================================================================

    [Test]
    public async Task ReviewCommentPr_DriverWithoutPrFileReview_DowngradesToPlainComment_WithAuditEvent()
    {
        Allow();
        // DG-2 check step: the resolved driver positively lacks PrFileReview —
        // the anchored post is never attempted.
        ResolveDriver(GitCredentialSources.Byok,
            new HashSet<PlatformCapability> { PlatformCapability.PrLifecycle });
        string? postedBody = null;
        _client.Setup(c => c.CreateIssueCommentAsync("acme", "widgets", "15", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, CancellationToken>((_, _, _, b, _) => postedBody = b)
            .ReturnsAsync(Ok(new IssueComment("88", "x", "bot", DateTimeOffset.UtcNow)));

        var body = new PrReviewCommentRequest { Body = "nit: rename this", CommitId = "sha1", Path = "src/a.cs", Line = 5, CorrelationId = "corr-rc" };
        var result = await _sut.ReviewCommentOnPullRequestAsync(_tenant, Repo, 15, body);

        result.Success.Should().BeTrue("the feedback is NEVER dropped (DG-2)");
        result.Outcome.Should().Be("Commented");
        result.ReviewCommentDowngraded.Should().BeTrue();
        result.CommentId.Should().Be(88);
        postedBody.Should().Contain("src/a.cs:5", "the downgraded body carries file:line");
        postedBody.Should().Contain("nit: rename this");
        _client.Verify(c => c.CreatePullRequestReviewCommentAsync(
            It.IsAny<CreatePullRequestReviewCommentRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _events.Appended.Select(e => e.Type).Should().BeEquivalentTo(new[]
        {
            GitEventTypes.PrReviewCommentDowngraded,
            GitEventTypes.PrReviewCommentedSuccess,
        }, "an audited downgrade + exactly one terminal success");
    }

    [Test]
    public async Task ReviewCommentPr_AnchoringRejected_DowngradesToPlainComment()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        // §4.3 safety net: the platform rejects the anchor (line not in diff).
        _client.Setup(c => c.CreatePullRequestReviewCommentAsync(
                It.IsAny<CreatePullRequestReviewCommentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<IssueComment>(new PlatformError.InvalidRequest(
                "invalid_request", "line 5 is not part of the diff")));
        _client.Setup(c => c.CreateIssueCommentAsync("acme", "widgets", "15", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new IssueComment("89", "x", "bot", DateTimeOffset.UtcNow)));

        var body = new PrReviewCommentRequest { Body = "nit", CommitId = "sha1", Path = "a.cs", Line = 5, CorrelationId = "corr-rc" };
        var result = await _sut.ReviewCommentOnPullRequestAsync(_tenant, Repo, 15, body);

        result.Success.Should().BeTrue();
        result.ReviewCommentDowngraded.Should().BeTrue();
        _events.Appended.Select(e => e.Type).Should().Contain(GitEventTypes.PrReviewCommentDowngraded);
    }

    [Test]
    public async Task ReviewCommentPr_RealFailure_IsNotDowngraded()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        // §4.5 — an auth failure is a REAL failure; downgrading it would hide
        // a broken credential behind a plain comment.
        _client.Setup(c => c.CreatePullRequestReviewCommentAsync(
                It.IsAny<CreatePullRequestReviewCommentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<IssueComment>(new PlatformError.AuthExpired()));

        var body = new PrReviewCommentRequest { Body = "nit", CommitId = "sha1", Path = "a.cs", Line = 5, CorrelationId = "corr-rc" };
        var result = await _sut.ReviewCommentOnPullRequestAsync(_tenant, Repo, 15, body);

        result.Success.Should().BeFalse();
        _client.Verify(c => c.CreateIssueCommentAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrReviewCommentedFailed);
    }

    [Test]
    public async Task ReviewCommentPr_DowngradeFailingToo_FailsLoud_NeverSilentlyDrops()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok,
            new HashSet<PlatformCapability> { PlatformCapability.PrLifecycle });
        _client.Setup(c => c.CreateIssueCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<IssueComment>(new PlatformError.NotFound()));

        var body = new PrReviewCommentRequest { Body = "nit", CommitId = "sha1", Path = "a.cs", Line = 5, CorrelationId = "corr-rc" };
        var result = await _sut.ReviewCommentOnPullRequestAsync(_tenant, Repo, 15, body);

        result.Success.Should().BeFalse("a dropped review comment must be LOUD");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrReviewCommentedFailed);
    }

    [Test]
    public async Task CreatePr_ReviewerRequestUnsupported_SkipsWithLabelAndAuditEvent_PrStepSucceeds()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        NoExistingOpenPr();
        _client.Setup(c => c.OpenPullRequestAsync(It.IsAny<OpenPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr()));
        _client.Setup(c => c.RequestReviewersAsync(It.IsAny<RequestReviewersRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<PullRequest>(new PlatformError.InvalidRequest(
                "capability_unsupported", "no reviewer API")));
        AddPullRequestLabelsRequest? labelCall = null;
        _client.Setup(c => c.AddPullRequestLabelsAsync(It.IsAny<AddPullRequestLabelsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AddPullRequestLabelsRequest, CancellationToken>((r, _) => labelCall = r)
            .ReturnsAsync(Ok(Pr()));

        var body = new CreatePrRequest
        {
            Title = "[ADL] #7: thing", Body = "body", HeadRef = "feature", BaseRef = "main",
            Reviewers = new[] { "alice" }, CorrelationId = "corr-pr",
        };
        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, body);

        result.Success.Should().BeTrue("DG-3 — a skipped reviewer request must NOT fail the PR step");
        result.ReviewersSkipped.Should().BeTrue();
        labelCall.Should().NotBeNull("the skip labels the PR for a human");
        labelCall!.Labels.Should().Contain(Tamma.Activities.ADL.CreatePullRequestActivity.ReviewersSkippedLabel);
        _events.Appended.Select(e => e.Type).Should().BeEquivalentTo(new[]
        {
            GitEventTypes.PrReviewersSkipped,
            GitEventTypes.PrOpenedSuccess,
        }, "an audited skip + exactly one terminal success");
    }

    [Test]
    public async Task CreatePr_NoReviewersRequested_EmitsNoSkipEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        NoExistingOpenPr();
        _client.Setup(c => c.OpenPullRequestAsync(It.IsAny<OpenPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr()));

        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, PrBody());

        result.Success.Should().BeTrue();
        result.ReviewersSkipped.Should().BeNull("no reviewer request was made — nothing was skipped");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrOpenedSuccess);
    }

    [Test]
    public async Task MergePr_MethodUnsupported_FallsBackAlongFixedOrder_WithAuditEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.GetPullRequestAsync("acme", "widgets", "15", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr(state: PullRequestState.Open, mergeable: true)));
        var attempted = new List<MergeMethod>();
        _client.Setup(c => c.MergePullRequestAsync(It.IsAny<MergePullRequestRequest>(), It.IsAny<CancellationToken>()))
            .Callback<MergePullRequestRequest, CancellationToken>((r, _) => attempted.Add(r.Method))
            .ReturnsAsync((MergePullRequestRequest r, CancellationToken _) =>
                r.Method == MergeMethod.Rebase
                    ? Fail<PullRequest>(new PlatformError.InvalidRequest(
                        "merge_method_unsupported", "rebase not allowed on this project"))
                    : Ok(Pr(state: PullRequestState.Merged, mergeSha: "sha-merged")));

        var body = new MergePrRequest
        {
            MergeStrategy = "rebase", IssueNumber = 0, BranchName = "feature",
            AutoDeleteBranch = false, CloseAssociatedIssue = false, CorrelationId = "corr-merge",
        };
        var result = await _sut.MergePullRequestAsync(_tenant, Repo, 15, body);

        result.Success.Should().BeTrue("DG-4 — the fallback merges instead of failing");
        result.MergeSha.Should().Be("sha-merged");
        result.AppliedMergeStrategy.Should().Be("squash", "rebase→squash is the first fallback hop");
        attempted.Should().Equal(MergeMethod.Rebase, MergeMethod.Squash);
        _events.Appended.Select(e => e.Type).Should().BeEquivalentTo(new[]
        {
            GitEventTypes.PrMergeMethodFallback,
            GitEventTypes.PrMergedSuccess,
        }, "an audited fallback + exactly one terminal success");
    }

    [Test]
    public async Task MergePr_RealFailure_DoesNotConsumeTheFallback()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.GetPullRequestAsync("acme", "widgets", "15", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr(state: PullRequestState.Open, mergeable: true)));
        _client.Setup(c => c.MergePullRequestAsync(It.IsAny<MergePullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<PullRequest>(new PlatformError.InvalidRequest(
                "merge_conflict", "409: merge conflict")));

        var result = await _sut.MergePullRequestAsync(_tenant, Repo, 15, MergeBody());

        result.Success.Should().BeFalse("§4.5 — only the exact typed code is consumed by the fallback");
        _client.Verify(c => c.MergePullRequestAsync(It.IsAny<MergePullRequestRequest>(), It.IsAny<CancellationToken>()),
            Times.Once, "a real failure fails loud immediately — no method roulette");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrMergeFailed);
    }

    [Test]
    public async Task MergePr_EveryMethodUnsupported_FailsLoud()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok);
        _client.Setup(c => c.GetPullRequestAsync("acme", "widgets", "15", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr(state: PullRequestState.Open, mergeable: true)));
        _client.Setup(c => c.MergePullRequestAsync(It.IsAny<MergePullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<PullRequest>(new PlatformError.InvalidRequest(
                "merge_method_unsupported", "nope")));

        var result = await _sut.MergePullRequestAsync(_tenant, Repo, 15, MergeBody());

        result.Success.Should().BeFalse("DG-4 — fail loud only when NO method works");
        _client.Verify(c => c.MergePullRequestAsync(It.IsAny<MergePullRequestRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3), "the full fixed order (requested + remaining) was tried");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrMergeFailed);
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    private static void AssertTags(DomainEvent evt, string operation, string credentialSource, string correlationId)
    {
        using var doc = JsonDocument.Parse(evt.Tags);
        var root = doc.RootElement;
        root.GetProperty("repo").GetString().Should().Be(Repo);
        root.GetProperty("operation").GetString().Should().Be(operation);
        root.GetProperty("credentialSource").GetString().Should().Be(credentialSource);
        root.GetProperty("correlationId").GetString().Should().Be(correlationId);
        root.TryGetProperty("tenantId", out _).Should().BeTrue();
    }

    private static int StatusOf(Microsoft.AspNetCore.Http.IResult result)
    {
        // Results.Ok(...) → Ok<T> (200); Results.Json(..., statusCode) → JsonHttpResult<T>.
        var prop = result.GetType().GetProperty("StatusCode");
        var value = prop?.GetValue(result);
        return value is int code ? code : 200;
    }

    private sealed class RecordingEventRepository : IEventRepository
    {
        public ConcurrentBag<DomainEvent> Appended { get; } = new();

        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            Appended.Add(evt);
            return Task.FromResult(evt);
        }

        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
            => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }
}

/// <summary>Tiny functional helper so a fluent one-liner can pipe an
/// <c>IResult</c> through <c>StatusOf</c> in the assertions above.</summary>
internal static class GitTestPipe
{
    public static TOut Let<TIn, TOut>(this TIn value, Func<TIn, TOut> f) => f(value);
}
