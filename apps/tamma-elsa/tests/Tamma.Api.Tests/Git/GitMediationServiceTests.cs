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
/// Story 38-1 — <see cref="GitMediationService"/> composition (guard → token →
/// platform-with-resolved-token → one DCB event). Every collaborator is faked;
/// assertions cover the fail-closed order (guard FIRST — deny ⇒ no token
/// resolved, platform never invoked), BYOK vs platform <c>credentialSource</c>,
/// the typed key-free failure taxonomy with a preserved <c>platformStatusCode</c>,
/// the 503 token-unavailable path, the exactly-one-terminal-event invariant + its
/// tags, and credential safety (the resolved token appears in no result / event).
/// </summary>
[TestFixture]
public class GitMediationServiceTests
{
    private const string SecretToken = "ghp-SUPER-SECRET-DO-NOT-LEAK-1234567890";
    private const string Repo = "acme/widgets";

    private Mock<IGitRepoAuthorizer> _authorizer = null!;
    private Mock<IGitTokenResolver> _tokenResolver = null!;
    private Mock<IGitHubClientFactory> _factory = null!;
    private Mock<IGitHubIntegrationService> _github = null!;
    private RecordingEventRepository _events = null!;
    private GitMediationService _sut = null!;
    private readonly Guid _tenant = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _authorizer = new Mock<IGitRepoAuthorizer>(MockBehavior.Strict);
        _tokenResolver = new Mock<IGitTokenResolver>(MockBehavior.Strict);
        _factory = new Mock<IGitHubClientFactory>(MockBehavior.Strict);
        _github = new Mock<IGitHubIntegrationService>(MockBehavior.Loose);
        _events = new RecordingEventRepository();

        _factory.Setup(f => f.Create(It.IsAny<string>())).Returns(_github.Object);

        _sut = new GitMediationService(
            _authorizer.Object, _tokenResolver.Object, _factory.Object, _events,
            NullLogger<GitMediationService>.Instance);
    }

    private void Allow() => _authorizer
        .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(GitRepoAuthorization.Allow());

    private void Deny() => _authorizer
        .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(GitRepoAuthorization.Deny("not authorized"));

    private void ResolveToken(string source) => _tokenResolver
        .Setup(t => t.ResolveAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new GitTokenResolution(SecretToken, source));

    private void NoToken() => _tokenResolver
        .Setup(t => t.ResolveAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((GitTokenResolution?)null);

    private static CreatePrRequest PrBody() => new()
    {
        Title = "[ADL] #7: thing", Body = "body", HeadRef = "feature", BaseRef = "main", CorrelationId = "corr-pr",
    };

    private static MergePrRequest MergeBody() => new()
    {
        MergeStrategy = "squash", IssueNumber = 7, BranchName = "feature", CorrelationId = "corr-merge",
    };

    private static CreateReleaseRequest ReleaseBody() => new()
    {
        TagName = "deploy-abc1234", TargetRef = "abc1234def", Name = "Release deploy-abc1234",
        Body = "notes", IssueNumber = 7, CorrelationId = "corr-release",
    };

    // ===================================================================
    // Cross-tenant guard (AC2) — FIRST, fail-closed
    // ===================================================================

    [Test]
    public async Task CreatePr_GuardDenied_403_NoTokenResolved_PlatformNeverCalled()
    {
        Deny();

        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, PrBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.RepoNotAuthorized);
        result.CredentialSource.Should().BeNull("no token is resolved on a guard denial");

        _tokenResolver.Verify(
            t => t.ResolveAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "the guard runs BEFORE token resolution");
        _factory.Verify(f => f.Create(It.IsAny<string>()), Times.Never,
            "no token-bound client is minted → the platform is never called");
        _github.VerifyNoOtherCalls();

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
    // Token resolution (AC3) — BYOK / platform / fail-closed
    // ===================================================================

    [Test]
    public async Task CreatePr_Success_Byok_UsesResolvedToken_EmitsOneSuccessEvent()
    {
        Allow();
        ResolveToken(GitCredentialSources.Byok);
        _github.Setup(g => g.GetGitHubOpenPullRequestForBranchAsync(Repo, "feature", "main"))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestRef?>.Ok(null));
        _github.Setup(g => g.CreateGitHubPullRequestAsync(Repo, It.IsAny<CreatePullRequestRequest>()))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestResult>.Ok(
                new GitHubPullRequestResult { Success = true, Number = 42, Url = "https://gh/pr/42" }));

        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, PrBody());

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Created");
        result.PrNumber.Should().Be(42);
        result.PrUrl.Should().Be("https://gh/pr/42");
        result.CredentialSource.Should().Be(GitCredentialSources.Byok);

        // The invariant: the token USED (minted into the client) == the token RESOLVED.
        _factory.Verify(f => f.Create(SecretToken), Times.Once);

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
        ResolveToken(GitCredentialSources.Platform);
        _github.Setup(g => g.GetGitHubOpenPullRequestForBranchAsync(Repo, "feature", "main"))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestRef?>.Ok(null));
        _github.Setup(g => g.CreateGitHubPullRequestAsync(Repo, It.IsAny<CreatePullRequestRequest>()))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestResult>.Ok(
                new GitHubPullRequestResult { Success = true, Number = 9, Url = "u" }));

        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, PrBody());

        result.CredentialSource.Should().Be(GitCredentialSources.Platform);
        _factory.Verify(f => f.Create(SecretToken), Times.Once);
    }

    [Test]
    public async Task CreatePr_TokenUnavailable_503_FailClosed_PlatformNeverCalled()
    {
        Allow();
        NoToken();

        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, PrBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.TokenUnavailable);
        _factory.Verify(f => f.Create(It.IsAny<string>()), Times.Never);

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
        ResolveToken(GitCredentialSources.Platform);
        _github.Setup(g => g.GetGitHubOpenPullRequestForBranchAsync(Repo, "feature", "main"))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestRef?>.Ok(null));
        _github.Setup(g => g.CreateGitHubPullRequestAsync(Repo, It.IsAny<CreatePullRequestRequest>()))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestResult>.Fail("409: a merge conflict"));

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
        ResolveToken(GitCredentialSources.Byok);
        _github.Setup(g => g.GetGitHubPullRequestAsync(Repo, 15))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestDetail>.Ok(
                new GitHubPullRequestDetail { Number = 15, State = "closed", Merged = false }));

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
        ResolveToken(GitCredentialSources.Byok);
        _github.Setup(g => g.GetGitHubPullRequestAsync(Repo, 15))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestDetail>.Ok(
                new GitHubPullRequestDetail { Number = 15, State = "open", Merged = false, Mergeable = null }));
        _github.Setup(g => g.MergeGitHubPullRequestAsync(Repo, 15, "squash"))
            .ReturnsAsync(IntegrationResult<GitHubMergeResult>.Fail("409: merge conflict"));

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
        ResolveToken(GitCredentialSources.Byok);
        _github.Setup(g => g.GetGitHubPullRequestAsync(Repo, 15))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestDetail>.Ok(
                new GitHubPullRequestDetail { Number = 15, State = "open", Merged = false, Mergeable = true }));
        _github.Setup(g => g.MergeGitHubPullRequestAsync(Repo, 15, "squash"))
            .ReturnsAsync(IntegrationResult<GitHubMergeResult>.Ok(new GitHubMergeResult { Success = true, MergeSha = "sha-123" }));
        _github.Setup(g => g.CloseGitHubIssueAsync(Repo, 7, It.IsAny<string>()))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));
        _github.Setup(g => g.DeleteGitHubBranchAsync(Repo, "feature"))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));

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
        ResolveToken(GitCredentialSources.Platform);
        _github.Setup(g => g.GetPullRequestReviewCommentsAsync(Repo, 3))
            .ReturnsAsync(IntegrationResult<List<GitHubReviewComment>>.Ok(new List<GitHubReviewComment>
            {
                new() { Id = 1, Body = "this is a bug", Path = "a.cs", Line = 5, Author = "bob" },
                new() { Id = 2, Body = "lgtm", Author = "sue" },
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
        _factory.Verify(f => f.Create(It.IsAny<string>()), Times.Never);
        _github.VerifyNoOtherCalls();
    }

    [Test]
    public async Task UpdateIssue_Success_PostsCommentAndLabels_OneEvent()
    {
        Allow();
        ResolveToken(GitCredentialSources.Byok);
        _github.Setup(g => g.PostIssueCommentAsync(Repo, 8, "status")).ReturnsAsync(IntegrationResult<bool>.Ok(true));
        _github.Setup(g => g.AddIssueLabelsAsync(Repo, 8, It.IsAny<string[]>())).ReturnsAsync(IntegrationResult<bool>.Ok(true));

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
        ResolveToken(GitCredentialSources.Byok);
        _github.Setup(g => g.PostIssueCommentAsync(Repo, 8, "status"))
            .ReturnsAsync(IntegrationResult<bool>.Fail("403: forbidden"));

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
    public async Task CreateRelease_Success_Byok_UsesResolvedToken_EmitsOneSuccessEvent()
    {
        Allow();
        ResolveToken(GitCredentialSources.Byok);
        _github.Setup(g => g.CreateGitHubReleaseAsync(Repo, It.IsAny<ReleaseCreationRequest>()))
            .ReturnsAsync(IntegrationResult<GitHubReleaseResult>.Ok(
                new GitHubReleaseResult { Success = true, Id = 55, HtmlUrl = "https://gh/releases/55", TagName = "deploy-abc1234" }));

        var result = await _sut.CreateReleaseAsync(_tenant, Repo, ReleaseBody());

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Created");
        result.ReleaseId.Should().Be(55);
        result.ReleaseUrl.Should().Be("https://gh/releases/55");
        result.ReleaseTag.Should().Be("deploy-abc1234");
        result.CredentialSource.Should().Be(GitCredentialSources.Byok);

        // The invariant: the token USED (minted into the client) == the token RESOLVED.
        _factory.Verify(f => f.Create(SecretToken), Times.Once);

        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.ReleaseCreatedSuccess);
    }

    [Test]
    public async Task CreateRelease_PlatformError_200SuccessFalse_OneFailedEvent()
    {
        Allow();
        ResolveToken(GitCredentialSources.Platform);
        _github.Setup(g => g.CreateGitHubReleaseAsync(Repo, It.IsAny<ReleaseCreationRequest>()))
            .ReturnsAsync(IntegrationResult<GitHubReleaseResult>.Fail("422: tag already exists"));

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
        _factory.Verify(f => f.Create(It.IsAny<string>()), Times.Never);
        _github.VerifyNoOtherCalls();
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.ReleaseCreatedFailed);
    }

    [Test]
    public async Task CreateRelease_TokenUnavailable_503_FailClosed_PlatformNeverCalled()
    {
        Allow();
        NoToken();

        var result = await _sut.CreateReleaseAsync(_tenant, Repo, ReleaseBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.TokenUnavailable);
        _factory.Verify(f => f.Create(It.IsAny<string>()), Times.Never);
        result.ToHttpResult().Let(StatusOf).Should().Be(503);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.ReleaseCreatedFailed);
    }

    // ===================================================================
    // F3 — an unexpected exception (DB/decrypt/mint/transport) between the
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
    public async Task TokenResolverThrows_TypedPlatformError_OneFailedEvent()
    {
        Allow();
        _tokenResolver
            .Setup(t => t.ResolveAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("secret decrypt failed"));

        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, PrBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.PlatformError);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrOpenedFailed);
    }

    [Test]
    public async Task ClientFactoryThrows_TypedPlatformError_OneFailedEvent()
    {
        Allow();
        ResolveToken(GitCredentialSources.Byok);
        _factory.Setup(f => f.Create(It.IsAny<string>())).Throws(new InvalidOperationException("client mint failed"));

        var result = await _sut.MergePullRequestAsync(_tenant, Repo, 15, MergeBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.PlatformError);
        result.ToHttpResult().Let(StatusOf).Should().Be(200);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrMergeFailed);
    }

    // ===================================================================
    // Credential safety (AC3/AC7) — the token appears in no result / event
    // ===================================================================

    [Test]
    public async Task CredentialSafety_ResolvedToken_NeverAppearsInResultOrEvent()
    {
        Allow();
        ResolveToken(GitCredentialSources.Byok);
        _github.Setup(g => g.GetGitHubOpenPullRequestForBranchAsync(Repo, "feature", "main"))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestRef?>.Ok(null));
        _github.Setup(g => g.CreateGitHubPullRequestAsync(Repo, It.IsAny<CreatePullRequestRequest>()))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestResult>.Ok(
                new GitHubPullRequestResult { Success = true, Number = 1, Url = "u" }));

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

    private static GitHubPullRequestDetail PrDetail(string state, bool isDraft = false) =>
        new() { Number = 15, State = state, IsDraft = isDraft };

    [Test]
    public async Task ClosePr_Success_EmitsExactlyOnePrClosedSuccessEvent()
    {
        Allow();
        ResolveToken(GitCredentialSources.Byok);
        _github.Setup(g => g.ClosePullRequestAsync(Repo, 15))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestDetail>.Ok(PrDetail("closed")));

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
        ResolveToken(GitCredentialSources.Byok);
        _github.Setup(g => g.ReopenPullRequestAsync(Repo, 15))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestDetail>.Ok(PrDetail("open")));

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
        ResolveToken(GitCredentialSources.Byok);
        _github.Setup(g => g.PostIssueCommentAsync(Repo, 15, "hello"))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));

        var result = await _sut.CommentOnPullRequestAsync(_tenant, Repo, 15, new PrCommentRequest { Body = "hello", CorrelationId = "corr-cmt" });

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Commented");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrCommentedSuccess);
    }

    [Test]
    public async Task ReviewCommentPr_Success_EmitsExactlyOnePrReviewCommentedSuccessEvent()
    {
        Allow();
        ResolveToken(GitCredentialSources.Byok);
        _github.Setup(g => g.PostPullRequestReviewCommentAsync(Repo, 15, "nit", "sha1", "a.cs", 5, "RIGHT"))
            .ReturnsAsync(IntegrationResult<GitHubReviewComment>.Ok(new GitHubReviewComment { Id = 77, Body = "nit", Path = "a.cs", Line = 5 }));

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
        ResolveToken(GitCredentialSources.Byok);
        _github.Setup(g => g.RequestReviewersAsync(Repo, 15, It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));

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
        ResolveToken(GitCredentialSources.Byok);
        _github.Setup(g => g.AddIssueLabelsAsync(Repo, 15, It.IsAny<string[]>()))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));
        _github.Setup(g => g.RemoveIssueLabelAsync(Repo, 15, "stale"))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));

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
        ResolveToken(GitCredentialSources.Byok);
        _github.Setup(g => g.SetPullRequestDraftAsync(Repo, 15, true))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestDetail>.Ok(PrDetail("open", isDraft: true)));

        var result = await _sut.SetPullRequestDraftAsync(_tenant, Repo, 15, new PrDraftRequest { Draft = true, CorrelationId = "corr-draft" });

        result.Success.Should().BeTrue();
        result.IsDraft.Should().Be(true);
        result.Outcome.Should().Be("DraftSet");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrDraftSetSuccess);
    }

    // ── Guard-deny: close AND draft (the GET-bearing verb) — REPO_NOT_AUTHORIZED,
    //    platform verb NEVER called, token resolver NEVER called ──

    [Test]
    public async Task ClosePr_GuardDenied_NeverResolvesToken_PlatformNeverCalled()
    {
        Deny();

        var result = await _sut.ClosePullRequestAsync(_tenant, Repo, 15, new ClosePrRequest { CorrelationId = "corr-close" });

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.RepoNotAuthorized);
        _tokenResolver.Verify(t => t.ResolveAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _github.Verify(g => g.ClosePullRequestAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        _github.VerifyNoOtherCalls();
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrClosedFailed);
    }

    [Test]
    public async Task DraftPr_GuardDenied_NeverResolvesToken_PlatformNeverCalled()
    {
        Deny();

        var result = await _sut.SetPullRequestDraftAsync(_tenant, Repo, 15, new PrDraftRequest { Draft = true, CorrelationId = "corr-draft" });

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.RepoNotAuthorized);
        _tokenResolver.Verify(t => t.ResolveAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _github.Verify(g => g.SetPullRequestDraftAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
        _github.VerifyNoOtherCalls();
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrDraftSetFailed);
    }

    // ── Token-unavailable: close AND review-comment — GIT_TOKEN_UNAVAILABLE
    //    fail-closed, exactly one FAILED event, platform verb never called ──

    [Test]
    public async Task ClosePr_TokenUnavailable_FailsClosed_PlatformNeverCalled()
    {
        Allow();
        NoToken();

        var result = await _sut.ClosePullRequestAsync(_tenant, Repo, 15, new ClosePrRequest { CorrelationId = "corr-close" });

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.TokenUnavailable);
        _factory.Verify(f => f.Create(It.IsAny<string>()), Times.Never);
        _github.Verify(g => g.ClosePullRequestAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrClosedFailed);
    }

    [Test]
    public async Task ReviewCommentPr_TokenUnavailable_FailsClosed_PlatformNeverCalled()
    {
        Allow();
        NoToken();

        var body = new PrReviewCommentRequest { Body = "nit", Path = "a.cs", Line = 5, CorrelationId = "corr-rc" };
        var result = await _sut.ReviewCommentOnPullRequestAsync(_tenant, Repo, 15, body);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.TokenUnavailable);
        _factory.Verify(f => f.Create(It.IsAny<string>()), Times.Never);
        _github.Verify(g => g.PostPullRequestReviewCommentAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(GitEventTypes.PrReviewCommentedFailed);
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
