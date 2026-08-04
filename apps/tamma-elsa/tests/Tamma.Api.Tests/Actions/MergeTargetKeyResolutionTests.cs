using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Git;
using Tamma.Core.Actions;
using Tamma.Data;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-12 AC3 — the merge gate resolves the per-target key from the PR's base
/// branch. Drives <see cref="MergeTargetActionKeySelector"/> once per target with a
/// fake mediation service returning each base, and pins the fail-closed cases
/// (unknown base and an unreadable PR both resolve to <c>git.merge.main</c>).
///
/// <para>RED against today's tree before the story: the per-target keys did not
/// exist and the merge route bound the coarse <c>git.pull-request.merge</c>, so the
/// asserted key was never produced.</para>
/// </summary>
[TestFixture]
public class MergeTargetKeyResolutionTests
{
    private static readonly ActionKey Dev = new(ActionNamespace.Effect, "git.merge.dev");
    private static readonly ActionKey Qa = new(ActionNamespace.Effect, "git.merge.qa");
    private static readonly ActionKey Main = new(ActionNamespace.Effect, "git.merge.main");
    private static readonly IReadOnlyList<ActionKey> Candidates = [Dev, Qa, Main];

    private static HttpContext MergeRequest()
    {
        var http = new DefaultHttpContext();
        http.Request.RouteValues["owner"] = "acme";
        http.Request.RouteValues["repo"] = "widget";
        http.Request.RouteValues["n"] = "7";
        return http;
    }

    private static MergeTargetActionKeySelector Selector(IGitMediationService git) =>
        new(git, new StubTenantContext(), NullLogger<MergeTargetActionKeySelector>.Instance);

    [TestCase("dev", "git.merge.dev")]
    [TestCase("qa", "git.merge.qa")]
    [TestCase("main", "git.merge.main")]
    public async Task ResolvesTheKeyFromThePrBaseBranch(string baseBranch, string expectedWire)
    {
        var selector = Selector(new FakeMediation(new GitMediationResult
        {
            Success = true,
            TargetBranch = baseBranch,
        }));

        var key = await selector.SelectAsync(MergeRequest(), Candidates, CancellationToken.None);

        key.Should().Be(new ActionKey(ActionNamespace.Effect, expectedWire));
    }

    [TestCase("master")]
    [TestCase("feature/x")]
    [TestCase("")]
    public async Task UnknownBase_ResolvesToMergeMain(string baseBranch)
    {
        var selector = Selector(new FakeMediation(new GitMediationResult
        {
            Success = true,
            TargetBranch = baseBranch,
        }));

        var key = await selector.SelectAsync(MergeRequest(), Candidates, CancellationToken.None);

        key.Should().Be(Main, "any base other than the dev/qa trunks fails closed to git.merge.main");
    }

    [Test]
    public async Task UnreadablePr_ResolvesToMergeMain_NeverFailsOpen()
    {
        // The mediation read failed (guard denied / token unavailable / platform error).
        var selector = Selector(new FakeMediation(new GitMediationResult
        {
            Success = false,
            FailureCode = "PLATFORM_ERROR",
        }));

        var key = await selector.SelectAsync(MergeRequest(), Candidates, CancellationToken.None);

        key.Should().Be(Main, "an unreadable PR is a DECISION (fail-closed to the strictest key), never fail-open");
    }

    [Test]
    public async Task MediationThrows_ResolvesToMergeMain()
    {
        var selector = Selector(new ThrowingMediation());

        var key = await selector.SelectAsync(MergeRequest(), Candidates, CancellationToken.None);

        key.Should().Be(Main, "a thrown read is a DECISION, not an evaluation error — it must not ride the fail-open arm");
    }

    [Test]
    public void MapBaseBranch_IsTotalAndFailsClosed()
    {
        MergeTargetActionKeySelector.MapBaseBranch("dev").ToWire().Should().Be("effect:git.merge.dev");
        MergeTargetActionKeySelector.MapBaseBranch(" qa ").ToWire().Should().Be("effect:git.merge.qa");
        MergeTargetActionKeySelector.MapBaseBranch("main").ToWire().Should().Be("effect:git.merge.main");
        MergeTargetActionKeySelector.MapBaseBranch(null).ToWire().Should().Be("effect:git.merge.main");
        MergeTargetActionKeySelector.MapBaseBranch("release/1.2").ToWire().Should().Be("effect:git.merge.main");
    }

    // ── fakes ──

    private sealed class StubTenantContext : ITenantContext
    {
        public Guid? TenantId { get; private set; } = Guid.NewGuid();
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private sealed class FakeMediation(GitMediationResult result) : NotImplementedMediation
    {
        public override Task<GitMediationResult> GetPullRequestAsync(
            Guid? tenantId, string repo, int prNumber, string correlationId, CancellationToken ct = default)
            => Task.FromResult(result);
    }

    private sealed class ThrowingMediation : NotImplementedMediation
    {
        public override Task<GitMediationResult> GetPullRequestAsync(
            Guid? tenantId, string repo, int prNumber, string correlationId, CancellationToken ct = default)
            => throw new InvalidOperationException("platform down");
    }

    private abstract class NotImplementedMediation : IGitMediationService
    {
        public virtual Task<GitMediationResult> GetPullRequestAsync(Guid? tenantId, string repo, int prNumber, string correlationId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> CreateBranchAsync(Guid? tenantId, string repo, CreateBranchRequest body, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> CreatePullRequestAsync(Guid? tenantId, string repo, CreatePrRequest body, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> MergePullRequestAsync(Guid? tenantId, string repo, int prNumber, MergePrRequest body, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> UpdateIssueAsync(Guid? tenantId, string repo, int issueNumber, UpdateIssueRequest body, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> GetPullRequestCommentsAsync(Guid? tenantId, string repo, int prNumber, string correlationId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> GetCommitsAsync(Guid? tenantId, string repo, string branch, DateTime? since, string correlationId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> GetFileChangesAsync(Guid? tenantId, string repo, string branch, string correlationId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> DeleteBranchAsync(Guid? tenantId, string repo, string branchName, string correlationId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> CreateReleaseAsync(Guid? tenantId, string repo, CreateReleaseRequest body, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> ClosePullRequestAsync(Guid? tenantId, string repo, int prNumber, ClosePrRequest body, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> ReopenPullRequestAsync(Guid? tenantId, string repo, int prNumber, ReopenPrRequest body, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> CommentOnPullRequestAsync(Guid? tenantId, string repo, int prNumber, PrCommentRequest body, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> ReviewCommentOnPullRequestAsync(Guid? tenantId, string repo, int prNumber, PrReviewCommentRequest body, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> RequestPullRequestReviewersAsync(Guid? tenantId, string repo, int prNumber, PrReviewersRequest body, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> UpdatePullRequestLabelsAsync(Guid? tenantId, string repo, int prNumber, PrLabelsRequest body, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitMediationResult> SetPullRequestDraftAsync(Guid? tenantId, string repo, int prNumber, PrDraftRequest body, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
