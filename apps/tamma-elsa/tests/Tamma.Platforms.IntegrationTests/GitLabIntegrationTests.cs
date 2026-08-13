using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitLab;
using Tamma.Platforms.IntegrationTests.Fixtures;

namespace Tamma.Platforms.IntegrationTests;

/// <summary>
/// Epic 31 P6 M2 — the GitLab integration suite, made REAL (the Story
/// 31-10 placeholder stub replaced). Boots a pinned
/// <c>gitlab/gitlab-ce</c> container via
/// <see cref="GitLabContainerFixture"/>, constructs the production driver
/// through the DI-registered <see cref="IGitPlatformDriverFactory"/>
/// (version probe included), and exercises the core verbs, the six P6
/// lifecycle verbs, a line comment on a genuinely MULTI-COMMIT MR (the
/// diff_refs hardening), and the pipelines surface against the live API.
///
/// <para><b>Nightly-gated</b> like the Gitea E2E: the image is ~3&#160;GB
/// and omnibus first-boot takes minutes, so the per-PR job's
/// <c>TestCategory!=Nightly</c> filter excludes this fixture; the
/// <c>gitlab-nightly</c> job (schedule + manual dispatch +
/// <c>run-gitlab-integration</c> PR label) runs it via
/// <c>FullyQualifiedName~GitLabIntegration</c>.</para>
///
/// <para>Skip-vs-fail mirrors the harness convention: no docker ⇒
/// <see cref="Assert.Ignore"/> locally, hard failure under
/// <c>PLATFORMS_REQUIRE_DOCKER=true</c> (see <see cref="DockerAvailability"/>).</para>
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("Platforms")]
[Category("GitLab")]
[Category("Nightly")]
public class GitLabIntegrationTests
{
    private GitLabContainerFixture _fixture = null!;
    private IGitPlatformDriver _driver = null!;
    private ServiceProvider _services = null!;

    private string Owner => _fixture.OwnerLogin;
    private string Repo => _fixture.RepoName;

    // The lifecycle scenario is one linear story across ordered tests.
    private static string _branch = string.Empty;
    private static string _prNumber = string.Empty;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        DockerAvailability.RequireOrSkip();

        _fixture = new GitLabContainerFixture();
        try
        {
            await _fixture.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            TestContext.Error.WriteLine($"GitLabContainerFixture.StartAsync failed: {ex}");
            throw;
        }

        _services = BuildDriverServices();
        var factory = _services.GetRequiredKeyedService<IGitPlatformDriverFactory>(PlatformKind.GitLab);
        var installation = new PlatformInstallation(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Kind: PlatformKind.GitLab,
            BaseUrl: _fixture.BaseUrl,
            InstallationExternalId: null);
        _driver = await factory.CreateAsync(
            installation, _fixture.BotToken, CancellationToken.None);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_fixture is not null) await _fixture.DisposeAsync();
    }

    private static ServiceProvider BuildDriverServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddGitLabPlatform();
        return services.BuildServiceProvider();
    }

    private void RequireReady()
    {
        if (!_fixture.IsReady)
        {
            Assert.Inconclusive(
                "GitLabContainerFixture did not reach ready state — see " +
                "OneTimeSetUp logs for the boot/seed failure.");
        }
    }

    // ───────────── capability detection ─────────────

    [Test, Order(1), Timeout(300_000)]
    public void Driver_DetectsVersion_AndAdvertisesPrLifecycle()
    {
        RequireReady();

        _fixture.DetectedVersion.Should().NotBeNull("the fixture read /api/v4/version");
        (_fixture.DetectedVersion! >= GitLabPlatformDriverTestHook.MinimumPrLifecycleVersion)
            .Should().BeTrue($"{_fixture.DetectedVersion} must be at or above the 13.9 lifecycle floor");
        _driver.Capabilities.Should().Contain(PlatformCapability.PrLifecycle,
            $"{_fixture.DetectedVersion} is above the 13.9 floor");
        _driver.Capabilities.Should().Contain(PlatformCapability.Actions);
        _driver.Actions.Should().NotBeNull();
    }

    // ───────────── core verbs ─────────────

    [Test, Order(2), Timeout(300_000)]
    public async Task GetRepo_ReadsTheFixtureProject()
    {
        RequireReady();
        var result = await _driver.Client.GetRepoAsync(Owner, Repo);
        var repo = result.Should().BeOfType<PlatformResult<Repo>.Ok>().Which.Value;
        repo.Name.Should().Be(Repo);
        repo.DefaultBranch.Should().Be(_fixture.DefaultBranch);
    }

    [Test, Order(3), Timeout(300_000)]
    public async Task ListBranches_And_ReadReadme()
    {
        RequireReady();

        var branches = await _driver.Client.ListRepoBranchesAsync(Owner, Repo);
        branches.Should().BeOfType<PlatformResult<IReadOnlyList<Branch>>.Ok>()
            .Which.Value.Should().Contain(b => b.Name == _fixture.DefaultBranch);

        var content = await _driver.Client.GetFileContentAsync(
            new GetFileContentRequest(Owner, Repo, "README.md", _fixture.DefaultBranch));
        content.Should().BeOfType<PlatformResult<byte[]>.Ok>()
            .Which.Value.Should().NotBeEmpty();
    }

    [Test, Order(4), Timeout(300_000)]
    public async Task CreateBranch_ThenReadItBack()
    {
        RequireReady();

        _branch = $"tamma/p6-{Guid.NewGuid():N}"[..24];
        var created = await _driver.Client.CreateBranchAsync(
            new CreateBranchRequest(Owner, Repo, _branch, _fixture.DefaultBranchSha));
        created.Should().BeOfType<PlatformResult<Branch>.Ok>()
            .Which.Value.Name.Should().Be(_branch);

        var read = await _driver.Client.GetBranchAsync(Owner, Repo, _branch);
        read.Should().BeOfType<PlatformResult<Branch>.Ok>();
    }

    // ───────────── the P6 lifecycle scenario (one linear story) ─────────────

    [Test, Order(5), Timeout(300_000)]
    public async Task OpenDraftMr_OnATwoCommitBranch()
    {
        RequireReady();

        // TWO commits on the source branch — the diff_refs shape (base ≠
        // start ≠ head) the old single-SHA position 400'd on.
        await _fixture.CommitFileAsync(
            _branch, "src/demo.txt", "line1\nline2\nline3\n", "P6 commit 1");
        await _fixture.CommitFileAsync(
            _branch, "src/demo2.txt", "alpha\nbeta\n", "P6 commit 2");

        var opened = await _driver.Client.OpenPullRequestAsync(
            new OpenPullRequestRequest(
                Owner, Repo,
                Title: "P6 lifecycle scenario",
                Body: "Two-commit MR for the diff_refs + lifecycle legs",
                SourceBranch: _branch,
                TargetBranch: _fixture.DefaultBranch,
                IsDraft: true));

        var pr = opened.Should().BeOfType<PlatformResult<PullRequest>.Ok>().Which.Value;
        pr.IsDraft.Should().BeTrue("the driver opened it with the Draft: title prefix");
        pr.Title.Should().StartWith("Draft: ");
        _prNumber = pr.Number;
    }

    [Test, Order(6), Timeout(300_000)]
    public async Task LineComment_OnTheMultiCommitMr_AnchorsViaDiffRefs()
    {
        RequireReady();
        _prNumber.Should().NotBeNullOrEmpty("the MR opened in the previous step");

        var result = await _driver.Client.CreatePullRequestReviewCommentAsync(
            new CreatePullRequestReviewCommentRequest(
                Owner, Repo, _prNumber,
                Path: "src/demo.txt", Line: 2,
                Body: "P6 anchored comment on a 2-commit MR",
                CommitSha: _fixture.DefaultBranchSha)); // deliberately NOT the head — diff_refs must win

        result.Should().BeOfType<PlatformResult<IssueComment>.Ok>(
            "the driver anchors on the MR's real diff_refs, not the caller's SHA");
    }

    [Test, Order(7), Timeout(300_000)]
    public async Task RequestReviewers_ResolvesTheSeededUser_AndRefusesGhosts()
    {
        RequireReady();

        var ok = await _driver.Client.RequestReviewersAsync(
            new RequestReviewersRequest(
                Owner, Repo, _prNumber, [GitLabContainerFixture.ReviewerUsername]));
        ok.Should().BeOfType<PlatformResult<PullRequest>.Ok>(
            "tamma-reviewer exists and resolves through GET /users?username=");

        var ghost = await _driver.Client.RequestReviewersAsync(
            new RequestReviewersRequest(Owner, Repo, _prNumber, ["no-such-user-p6"]));
        ghost.Should().BeOfType<PlatformResult<PullRequest>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("reviewer_unresolvable");
    }

    [Test, Order(8), Timeout(300_000)]
    public async Task Labels_AddThenRemove_RoundTrip()
    {
        RequireReady();

        var added = await _driver.Client.AddPullRequestLabelsAsync(
            new AddPullRequestLabelsRequest(Owner, Repo, _prNumber, ["tamma-p6", "needs-review"]));
        added.Should().BeOfType<PlatformResult<PullRequest>.Ok>(
            "add_labels auto-creates missing project labels");

        var removed = await _driver.Client.RemovePullRequestLabelAsync(
            Owner, Repo, _prNumber, "needs-review");
        removed.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
    }

    [Test, Order(9), Timeout(300_000)]
    public async Task UnDraft_StripsThePrefix_AndIsIdempotent()
    {
        RequireReady();

        var ready = await _driver.Client.SetDraftAsync(
            new SetPullRequestDraftRequest(Owner, Repo, _prNumber, Draft: false));
        var pr = ready.Should().BeOfType<PlatformResult<PullRequest>.Ok>().Which.Value;
        pr.IsDraft.Should().BeFalse();
        pr.Title.Should().Be("P6 lifecycle scenario", "marking ready strips the Draft: prefix");

        var again = await _driver.Client.SetDraftAsync(
            new SetPullRequestDraftRequest(Owner, Repo, _prNumber, Draft: false));
        again.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.IsDraft.Should().BeFalse("already-ready is idempotent success");
    }

    [Test, Order(10), Timeout(300_000)]
    public async Task Merge_RebaseIsTypedUnsupported_SquashMerges_WithSha()
    {
        RequireReady();

        var rebase = await _driver.Client.MergePullRequestAsync(
            new MergePullRequestRequest(Owner, Repo, _prNumber, MergeMethod.Rebase));
        rebase.Should().BeOfType<PlatformResult<PullRequest>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("merge_method_unsupported",
                "the DG-4 fallback consumes exactly this code");

        // GitLab may briefly report the merge as unacceptable right after MR
        // creation (merge-check still running) — retry the squash briefly.
        PlatformResult<PullRequest>? merged = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            merged = await _driver.Client.MergePullRequestAsync(
                new MergePullRequestRequest(
                    Owner, Repo, _prNumber, MergeMethod.Squash,
                    CommitMessage: "P6 squash merge"));
            if (merged is PlatformResult<PullRequest>.Ok) break;
            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        var pr = merged.Should().BeOfType<PlatformResult<PullRequest>.Ok>().Which.Value;
        pr.State.Should().Be(PullRequestState.Merged);
        pr.MergeCommitSha.Should().NotBeNullOrEmpty(
            "the merge activity fails loud on a missing SHA — squash_commit_sha must map");
    }

    [Test, Order(11), Timeout(300_000)]
    public async Task CloseAndReopen_OnASecondMr()
    {
        RequireReady();

        var branch = $"tamma/p6-close-{Guid.NewGuid():N}"[..30];
        await _fixture.CommitFileAsync(
            branch, "src/close-me.txt", "x\n", "P6 close/reopen seed",
            startBranch: _fixture.DefaultBranch);

        var opened = await _driver.Client.OpenPullRequestAsync(
            new OpenPullRequestRequest(
                Owner, Repo, "P6 close/reopen",
                SourceBranch: branch, TargetBranch: _fixture.DefaultBranch, IsDraft: false));
        var number = opened.Should().BeOfType<PlatformResult<PullRequest>.Ok>().Which.Value.Number;

        var closed = await _driver.Client.ClosePullRequestAsync(Owner, Repo, number);
        closed.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.State.Should().Be(PullRequestState.Closed);

        var reopened = await _driver.Client.ReopenPullRequestAsync(Owner, Repo, number);
        reopened.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.State.Should().Be(PullRequestState.Open);
    }

    // ───────────── pipelines (Actions surface) ─────────────

    [Test, Order(12), Timeout(300_000)]
    public async Task Pipeline_Dispatch_Status_Cancel()
    {
        RequireReady();
        _driver.Actions.Should().NotBeNull();

        var dispatched = await _driver.Actions!.DispatchWorkflowAsync(
            Owner, Repo,
            new WorkflowDispatchRequest(
                WorkflowFileName: ".gitlab-ci.yml", // ignored by GitLab — pipeline-per-ref
                Ref: _fixture.DefaultBranch,
                Inputs: new Dictionary<string, string>()));
        var run = dispatched.Should().BeOfType<PlatformResult<WorkflowRun>.Ok>().Which.Value;
        run.RunId.Should().NotBeNullOrEmpty("dispatch must return a POLLABLE run id");

        var status = await _driver.Actions.GetRunStatusAsync(Owner, Repo, run.RunId);
        status.Should().BeOfType<PlatformResult<WorkflowRun>.Ok>()
            .Which.Value.RunId.Should().Be(run.RunId);
        // No runner is registered — the pipeline sits pending/created; that is
        // exactly what the poller sees between dispatch and completion.

        var canceled = await _driver.Actions.CancelRunAsync(Owner, Repo, run.RunId);
        canceled.Should().BeOfType<PlatformResult<bool>.Ok>();
    }

    [Test, Order(13), Timeout(300_000)]
    public async Task ListAccessibleRepos_YieldsTheFixtureProject()
    {
        RequireReady();

        var found = false;
        await foreach (var repo in _driver.Client.ListAccessibleReposAsync())
        {
            if (string.Equals(repo.Name, Repo, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                break;
            }
        }
        found.Should().BeTrue("the credential owns the fixture project");
    }
}

/// <summary>
/// The driver's version floor without InternalsVisibleTo — mirrors
/// <c>GitLabPlatformDriver.MinimumPrLifecycleVersion</c>; the unit suite
/// pins the real constant, this hook only keeps the assertion readable.
/// </summary>
internal static class GitLabPlatformDriverTestHook
{
    public static readonly Version MinimumPrLifecycleVersion = new(13, 9);
}
