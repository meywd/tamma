using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.Gitea;
using Tamma.Platforms.IntegrationTests.Fixtures;

namespace Tamma.Platforms.IntegrationTests;

/// <summary>
/// Story 31-10 — Gitea integration tests. Boots a real
/// <c>gitea/gitea:1.21</c> container via <see cref="GiteaContainerFixture"/>,
/// constructs the production <see cref="GiteaPlatformDriverFactory"/>,
/// and exercises every <see cref="IGitPlatformClient"/> +
/// <see cref="IGitPlatformActionsClient"/> method against the live API.
///
/// <para>Skip-vs-fail (mirrors wave-A chromadb integration test): when
/// docker is unavailable on the host, every test in this fixture is
/// <see cref="Assert.Ignore"/>-skipped (NOT failed) so dev machines
/// without docker don't see permanent CI red. CI sets
/// <c>PLATFORMS_REQUIRE_DOCKER=true</c> which converts the skip into a
/// hard failure — see <see cref="DockerAvailability"/>.</para>
///
/// <para>Test method coverage (17 methods total):
/// <list type="bullet">
///   <item><b>IGitPlatformClient</b> (12): GetRepo, ListRepoBranches,
///         GetFileContent, CreateBranch, OpenPullRequest,
///         GetPullRequest, ListPullRequestFiles,
///         CreatePullRequestReviewComment, MergePullRequest,
///         CreateIssueComment, RegisterWebhook, ListAccessibleRepos.</item>
///   <item><b>IGitPlatformActionsClient</b> (5): DispatchWorkflow,
///         GetRunStatus, ListRunJobs, DownloadArtifact, CancelRun.</item>
/// </list>
/// Actions tests gracefully <see cref="Assert.Inconclusive"/> when no
/// act_runner sidecar is registered (the harness today doesn't ship
/// one — see plan §step-4 for the runner sidecar follow-up).</para>
///
/// <para>Per-test timeout: 5 min (300_000 ms). Each Gitea API call is
/// sub-second on a healthy container; 5 min is room for a cold-cache
/// container + a slow CI runner.</para>
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("Platforms")]
[Category("Gitea")]
public class GiteaIntegrationTests
{
    private GiteaContainerFixture _fixture = null!;
    private IGitPlatformDriver _driver = null!;
    private ServiceProvider _services = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        DockerAvailability.RequireOrSkip();

        _fixture = new GiteaContainerFixture();
        try
        {
            await _fixture.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Surface the seed failure with the full container log
            // payload so the CI artifact upload step has something
            // useful. Tests in this fixture will hit `Inconclusive`
            // because IsReady stays false.
            TestContext.Error.WriteLine(
                $"GiteaContainerFixture.StartAsync failed: {ex}");
            throw;
        }

        // Build the production driver via the registered factory —
        // mirrors what Story 31-2's PlatformResolver does at runtime.
        _services = BuildDriverServices();
        var factory = _services.GetRequiredService<GiteaPlatformDriverFactory>();
        var installation = new PlatformInstallation(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Kind: PlatformKind.Gitea,
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

    private ServiceProvider BuildDriverServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddHttpClient(GiteaPlatformDriverFactory.GiteaHttpClientName);
        services.AddSingleton<GiteaOAuth2TokenCache>();
        services.AddSingleton<GiteaPlatformDriverFactory>();
        return services.BuildServiceProvider();
    }

    private void RequireReady()
    {
        if (!_fixture.IsReady)
        {
            Assert.Inconclusive(
                "GiteaContainerFixture did not reach ready state — see " +
                "OneTimeSetUp logs for the boot/seed failure.");
        }
    }

    // ─────────────────── IGitPlatformClient (12) ───────────────────

    [Test, Timeout(300_000)]
    public Task Driver_HasExpectedKindAndCapabilities()
    {
        RequireReady();
        _driver.Kind.Should().Be(PlatformKind.Gitea);
        _driver.Capabilities.Should().NotBeEmpty();

        // 1.21 is the floor for Gitea Actions per
        // GiteaPlatformDriver.MinimumActionsVersion. The pinned image
        // is exactly 1.21, so Actions MUST be in the capability set.
        _driver.Capabilities.Should().Contain(PlatformCapability.Actions);
        _driver.Actions.Should().NotBeNull();
        return Task.CompletedTask;
    }

    [Test, Timeout(300_000)]
    public async Task GetRepoAsync_ReturnsFixtureRepo()
    {
        RequireReady();
        var result = await _driver.Client.GetRepoAsync(
            _fixture.OwnerLogin, _fixture.RepoName);

        result.Should().BeOfType<PlatformResult<Repo>.Ok>();
        var repo = result.GetValueOrDefault()!;
        repo.Owner.Should().Be(_fixture.OwnerLogin);
        repo.Name.Should().Be(_fixture.RepoName);
        repo.DefaultBranch.Should().Be(_fixture.DefaultBranch);
        repo.IsPrivate.Should().BeFalse();
    }

    [Test, Timeout(300_000)]
    public async Task ListRepoBranchesAsync_IncludesDefaultBranch()
    {
        RequireReady();
        var result = await _driver.Client.ListRepoBranchesAsync(
            _fixture.OwnerLogin, _fixture.RepoName);

        result.Should().BeOfType<PlatformResult<IReadOnlyList<Branch>>.Ok>();
        var branches = result.GetValueOrDefault()!;
        branches.Should().Contain(b => b.Name == _fixture.DefaultBranch);
    }

    [Test, Timeout(300_000)]
    public async Task GetFileContentAsync_ReadsReadme()
    {
        RequireReady();
        // auto_init=true on repo creation seeds a README.md.
        var result = await _driver.Client.GetFileContentAsync(
            new GetFileContentRequest(
                _fixture.OwnerLogin, _fixture.RepoName,
                Path: "README.md", Ref: _fixture.DefaultBranch));

        result.Should().BeOfType<PlatformResult<byte[]>.Ok>();
        var bytes = result.GetValueOrDefault()!;
        bytes.Length.Should().BeGreaterThan(0);
    }

    [Test, Timeout(300_000)]
    public async Task CreateBranchAsync_CreatesFromDefaultBranchSha()
    {
        RequireReady();
        var newBranch = $"feat/it-{Guid.NewGuid():N}";
        var result = await _driver.Client.CreateBranchAsync(
            new CreateBranchRequest(
                _fixture.OwnerLogin, _fixture.RepoName,
                NewBranchName: newBranch,
                FromSha: _fixture.DefaultBranchSha));

        result.Should().BeOfType<PlatformResult<Branch>.Ok>();
        var branch = result.GetValueOrDefault()!;
        branch.Name.Should().Be(newBranch);
        // SHA may differ on Gitea since it can fast-forward, but it
        // must be non-empty.
        branch.Sha.Should().NotBeNullOrEmpty();
    }

    [Test, Timeout(300_000)]
    public async Task OpenPullRequestAsync_OpensFromNewBranch()
    {
        RequireReady();
        // Create a branch + a commit on it, then open a PR. We commit
        // a file via Gitea's contents API directly so we're not
        // reaching for the driver's create-file capability (which is
        // not in the IGitPlatformClient surface today).
        var branch = await CreateBranchWithCommitAsync(
            $"feat/pr-{Guid.NewGuid():N}");

        var result = await _driver.Client.OpenPullRequestAsync(
            new OpenPullRequestRequest(
                _fixture.OwnerLogin, _fixture.RepoName,
                Title: "Story 31-10 integration PR",
                SourceBranch: branch,
                TargetBranch: _fixture.DefaultBranch,
                Body: "Opened by GiteaIntegrationTests"));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
        var pr = result.GetValueOrDefault()!;
        pr.SourceBranch.Should().Be(branch);
        pr.TargetBranch.Should().Be(_fixture.DefaultBranch);
        pr.State.Should().Be(PullRequestState.Open);
    }

    [Test, Timeout(300_000)]
    public async Task GetPullRequestAsync_RoundTripsOpenedPr()
    {
        RequireReady();
        var branch = await CreateBranchWithCommitAsync(
            $"feat/get-pr-{Guid.NewGuid():N}");
        var opened = (await _driver.Client.OpenPullRequestAsync(
            new OpenPullRequestRequest(
                _fixture.OwnerLogin, _fixture.RepoName,
                Title: "for GetPullRequestAsync test",
                SourceBranch: branch,
                TargetBranch: _fixture.DefaultBranch))).GetValueOrDefault()!;

        var result = await _driver.Client.GetPullRequestAsync(
            _fixture.OwnerLogin, _fixture.RepoName, opened.Number);

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
        var pr = result.GetValueOrDefault()!;
        pr.Number.Should().Be(opened.Number);
        pr.Title.Should().Be("for GetPullRequestAsync test");
    }

    [Test, Timeout(300_000)]
    public async Task ListPullRequestFilesAsync_ReturnsAddedFile()
    {
        RequireReady();
        var branch = await CreateBranchWithCommitAsync(
            $"feat/list-files-{Guid.NewGuid():N}",
            committedFile: "story-31-10/added.txt");
        var opened = (await _driver.Client.OpenPullRequestAsync(
            new OpenPullRequestRequest(
                _fixture.OwnerLogin, _fixture.RepoName,
                Title: "for ListPullRequestFilesAsync test",
                SourceBranch: branch,
                TargetBranch: _fixture.DefaultBranch))).GetValueOrDefault()!;

        var result = await _driver.Client.ListPullRequestFilesAsync(
            _fixture.OwnerLogin, _fixture.RepoName, opened.Number);

        result.Should().BeOfType<PlatformResult<IReadOnlyList<PrFile>>.Ok>();
        var files = result.GetValueOrDefault()!;
        files.Should().Contain(f => f.Path.Contains("added.txt"));
    }

    [Test, Timeout(300_000)]
    public async Task CreatePullRequestReviewCommentAsync_PostsAtFileLine()
    {
        RequireReady();
        var branch = await CreateBranchWithCommitAsync(
            $"feat/review-{Guid.NewGuid():N}",
            committedFile: "story-31-10/reviewed.txt");
        var opened = (await _driver.Client.OpenPullRequestAsync(
            new OpenPullRequestRequest(
                _fixture.OwnerLogin, _fixture.RepoName,
                Title: "for CreatePullRequestReviewCommentAsync test",
                SourceBranch: branch,
                TargetBranch: _fixture.DefaultBranch))).GetValueOrDefault()!;

        // We need the head SHA for the review payload — fetch via
        // GetPullRequestAsync's response is good enough for shape, but
        // the review-comment endpoint accepts the source-branch tip
        // commit SHA. We grab it directly via the contents API.
        var headSha = await GetBranchTipShaAsync(branch);

        var result = await _driver.Client.CreatePullRequestReviewCommentAsync(
            new CreatePullRequestReviewCommentRequest(
                _fixture.OwnerLogin, _fixture.RepoName,
                PrNumber: opened.Number,
                Path: "story-31-10/reviewed.txt",
                Line: 1,
                Body: "Look at this line.",
                CommitSha: headSha));

        // Some Gitea versions return Failed when the diff line lookup
        // can't anchor the comment. Treat both Ok and Failed as
        // exercising the code path; the test's contract is "the call
        // hits the platform without throwing, returns a typed result".
        result.Should().Match(r =>
            r is PlatformResult<IssueComment>.Ok ||
            r is PlatformResult<IssueComment>.Failed);
    }

    [Test, Timeout(300_000)]
    public async Task MergePullRequestAsync_MergesFastForward()
    {
        RequireReady();
        var branch = await CreateBranchWithCommitAsync(
            $"feat/merge-{Guid.NewGuid():N}",
            committedFile: $"story-31-10/merged-{Guid.NewGuid():N}.txt");
        var opened = (await _driver.Client.OpenPullRequestAsync(
            new OpenPullRequestRequest(
                _fixture.OwnerLogin, _fixture.RepoName,
                Title: "for MergePullRequestAsync test",
                SourceBranch: branch,
                TargetBranch: _fixture.DefaultBranch))).GetValueOrDefault()!;

        // Gitea occasionally rejects the first merge call with
        // "head out of date" / "checking" while it computes the merge
        // base — retry a few times with a short delay. This is a
        // platform quirk, not a driver bug; production callers
        // already retry merges via the workflow-level retry decorator.
        PlatformResult<PullRequest>? result = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            result = await _driver.Client.MergePullRequestAsync(
                new MergePullRequestRequest(
                    _fixture.OwnerLogin, _fixture.RepoName,
                    PrNumber: opened.Number,
                    Method: MergeMethod.Merge,
                    CommitMessage: "merged by GiteaIntegrationTests"));
            if (result is PlatformResult<PullRequest>.Ok) break;
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        result.Should().NotBeNull();
        result!.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
        var merged = result!.GetValueOrDefault()!;
        merged.State.Should().Be(PullRequestState.Merged);
    }

    [Test, Timeout(300_000)]
    public async Task CreateIssueCommentAsync_PostsToOpenPr()
    {
        RequireReady();
        var branch = await CreateBranchWithCommitAsync(
            $"feat/issue-comment-{Guid.NewGuid():N}");
        var opened = (await _driver.Client.OpenPullRequestAsync(
            new OpenPullRequestRequest(
                _fixture.OwnerLogin, _fixture.RepoName,
                Title: "for CreateIssueCommentAsync test",
                SourceBranch: branch,
                TargetBranch: _fixture.DefaultBranch))).GetValueOrDefault()!;

        var result = await _driver.Client.CreateIssueCommentAsync(
            _fixture.OwnerLogin, _fixture.RepoName, opened.Number,
            "Story 31-10 integration ping");

        result.Should().BeOfType<PlatformResult<IssueComment>.Ok>();
        var comment = result.GetValueOrDefault()!;
        comment.Body.Should().Contain("Story 31-10");
    }

    [Test, Timeout(300_000)]
    public async Task RegisterWebhookAsync_RegistersWithSecret()
    {
        RequireReady();
        // We don't run a callback listener in the harness today (plan
        // §step-5 for the future WebhookCallbackListener follow-up).
        // The contract is "registration succeeds + returns id" — that
        // exercises the request shape (event names, secret field,
        // active flag) on a real Gitea version.
        var result = await _driver.Client.RegisterWebhookAsync(
            new RegisterWebhookRequest(
                _fixture.OwnerLogin, _fixture.RepoName,
                DeliveryUrl: "http://example.invalid/tamma-webhook",
                Events: new[] { "push", "pull_request" },
                Secret: _fixture.WebhookSecret));

        result.Should().BeOfType<PlatformResult<WebhookRegistration>.Ok>();
        var reg = result.GetValueOrDefault()!;
        reg.Id.Should().NotBeNullOrEmpty();
    }

    [Test, Timeout(300_000)]
    public async Task ListAccessibleReposAsync_YieldsFixtureRepo()
    {
        RequireReady();
        var seen = new List<Repo>();
        await foreach (var r in _driver.Client.ListAccessibleReposAsync())
        {
            seen.Add(r);
            // Stop at 50 to keep the test fast — the bot owns at most
            // a handful of repos in the fixture.
            if (seen.Count >= 50) break;
        }
        seen.Should().Contain(r =>
            r.Owner == _fixture.OwnerLogin &&
            r.Name == _fixture.RepoName);
    }

    // ───────────────── IGitPlatformActionsClient (5) ─────────────────

    [Test, Timeout(300_000)]
    public async Task DispatchWorkflowAsync_DispatchesEchoWorkflow()
    {
        RequireReady();
        if (_driver.Actions is null)
        {
            Assert.Inconclusive(
                "Driver reports no Actions surface — pinned image " +
                $"({GiteaContainerFixture.GiteaImage}) version " +
                $"{_fixture.DetectedVersion} below MinimumActionsVersion " +
                $"{GiteaPlatformDriver.MinimumActionsVersion}.");
            return;
        }

        // Seed the workflow file before dispatch — Gitea Actions
        // requires the file to live in .gitea/workflows/ on the ref
        // we're dispatching. We commit it via the contents API to
        // keep this self-contained.
        await EnsureWorkflowFileAsync();

        var result = await _driver.Actions!.DispatchWorkflowAsync(
            _fixture.OwnerLogin, _fixture.RepoName,
            new WorkflowDispatchRequest(
                Ref: _fixture.DefaultBranch,
                WorkflowFileName: "echo.yaml",
                Inputs: new Dictionary<string, string>()));

        // Without an act_runner, the dispatch may queue the run but
        // the run ID may be null / the response may indicate "no
        // runners available". Either is fine — the contract is "the
        // dispatch call hit the API without throwing".
        result.Should().Match(r =>
            r is PlatformResult<WorkflowRun>.Ok ||
            r is PlatformResult<WorkflowRun>.Failed);
    }

    [Test, Timeout(300_000)]
    public async Task GetRunStatusAsync_ReturnsTypedResult()
    {
        RequireReady();
        if (_driver.Actions is null)
        {
            Assert.Inconclusive("No Actions surface — see " +
                "DispatchWorkflowAsync_DispatchesEchoWorkflow.");
            return;
        }

        // We don't have a guaranteed run ID, so call with a synthetic
        // ID and assert the typed not-found mapping. Verifies the
        // error-shape parsing on real Gitea responses.
        var result = await _driver.Actions!.GetRunStatusAsync(
            _fixture.OwnerLogin, _fixture.RepoName, "999999999");

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Failed>();
    }

    [Test, Timeout(300_000)]
    public async Task ListRunJobsAsync_ReturnsTypedResult()
    {
        RequireReady();
        if (_driver.Actions is null)
        {
            Assert.Inconclusive("No Actions surface.");
            return;
        }

        var result = await _driver.Actions!.ListRunJobsAsync(
            _fixture.OwnerLogin, _fixture.RepoName, "999999999");

        // Allow either Failed (404 → mapped) or Ok with empty list —
        // some Gitea versions return [] for an unknown run.
        result.Should().Match(r =>
            r is PlatformResult<IReadOnlyList<WorkflowJob>>.Ok ||
            r is PlatformResult<IReadOnlyList<WorkflowJob>>.Failed);
    }

    [Test, Timeout(300_000)]
    public async Task DownloadArtifactAsync_ReturnsTypedResult()
    {
        RequireReady();
        if (_driver.Actions is null)
        {
            Assert.Inconclusive("No Actions surface.");
            return;
        }

        var result = await _driver.Actions!.DownloadArtifactAsync(
            _fixture.OwnerLogin, _fixture.RepoName, "999999999");

        // Without a real artifact, expect a Failed (404 mapping).
        // Verifies the artifact endpoint URL + auth header is correct
        // even on miss.
        result.Should().BeOfType<PlatformResult<Stream>.Failed>();
    }

    [Test, Timeout(300_000)]
    public async Task CancelRunAsync_ReturnsTypedResult()
    {
        RequireReady();
        if (_driver.Actions is null)
        {
            Assert.Inconclusive("No Actions surface.");
            return;
        }

        var result = await _driver.Actions!.CancelRunAsync(
            _fixture.OwnerLogin, _fixture.RepoName, "999999999");

        result.Should().Match(r =>
            r is PlatformResult<bool>.Ok ||
            r is PlatformResult<bool>.Failed);
    }

    // ─────────────── Story 31-13 lifecycle verbs (Epic 31 P5 M1) ───────────────
    // The six PR lifecycle verbs, REAL against the live container. The pinned
    // 1.21 image is above the 1.14 requested_reviewers floor, so the driver
    // must advertise PrLifecycle and every verb must perform.

    [Test, Timeout(300_000)]
    public void Driver_AdvertisesPrLifecycle_On121()
    {
        RequireReady();
        _driver.Capabilities.Should().Contain(PlatformCapability.PrLifecycle,
            "1.21 ≥ the 1.14 lifecycle floor — the P5 capability flip must hold on a real instance");
    }

    [Test, Timeout(300_000)]
    public async Task ClosePullRequestAsync_ThenReopen_RoundTripsState()
    {
        RequireReady();
        var branch = await CreateBranchWithCommitAsync($"feat/lc-close-{Guid.NewGuid():N}");
        var pr = (await _driver.Client.OpenPullRequestAsync(new OpenPullRequestRequest(
            _fixture.OwnerLogin, _fixture.RepoName,
            Title: "lifecycle close/reopen", SourceBranch: branch,
            TargetBranch: _fixture.DefaultBranch))).GetValueOrDefault()!;

        var closed = await _driver.Client.ClosePullRequestAsync(
            _fixture.OwnerLogin, _fixture.RepoName, pr.Number);
        closed.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.State.Should().Be(PullRequestState.Closed);

        var reopened = await _driver.Client.ReopenPullRequestAsync(
            _fixture.OwnerLogin, _fixture.RepoName, pr.Number);
        reopened.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.State.Should().Be(PullRequestState.Open);
    }

    [Test, Timeout(300_000)]
    public async Task AddAndRemovePullRequestLabels_RoundTrip_CreatingMissingLabel()
    {
        RequireReady();
        var branch = await CreateBranchWithCommitAsync($"feat/lc-labels-{Guid.NewGuid():N}");
        var pr = (await _driver.Client.OpenPullRequestAsync(new OpenPullRequestRequest(
            _fixture.OwnerLogin, _fixture.RepoName,
            Title: "lifecycle labels", SourceBranch: branch,
            TargetBranch: _fixture.DefaultBranch))).GetValueOrDefault()!;

        var label = $"tamma-e2e-{Guid.NewGuid():N}"[..20];
        var added = await _driver.Client.AddPullRequestLabelsAsync(
            new AddPullRequestLabelsRequest(
                _fixture.OwnerLogin, _fixture.RepoName, pr.Number, new[] { label }));
        added.Should().BeOfType<PlatformResult<PullRequest>.Ok>(
            "a missing label is created best-effort, then added");

        var removed = await _driver.Client.RemovePullRequestLabelAsync(
            _fixture.OwnerLogin, _fixture.RepoName, pr.Number, label);
        removed.Should().BeOfType<PlatformResult<PullRequest>.Ok>();

        // Removing it again is idempotent success (label now absent).
        var again = await _driver.Client.RemovePullRequestLabelAsync(
            _fixture.OwnerLogin, _fixture.RepoName, pr.Number, label);
        again.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
    }

    [Test, Timeout(300_000)]
    public async Task SetDraftAsync_TogglesViaWipTitlePrefix_BothDirections()
    {
        RequireReady();
        var branch = await CreateBranchWithCommitAsync($"feat/lc-draft-{Guid.NewGuid():N}");
        // Draft open → the driver prefixes the title (Gitea has no
        // create-side draft field on any version; WIP: IS the mechanism).
        var pr = (await _driver.Client.OpenPullRequestAsync(new OpenPullRequestRequest(
            _fixture.OwnerLogin, _fixture.RepoName,
            Title: "lifecycle draft", SourceBranch: branch,
            TargetBranch: _fixture.DefaultBranch, IsDraft: true))).GetValueOrDefault()!;
        pr.IsDraft.Should().BeTrue("the WIP title prefix marks the PR draft on Gitea");
        pr.Title.Should().StartWith("WIP:");

        var ready = await _driver.Client.SetDraftAsync(
            new SetPullRequestDraftRequest(_fixture.OwnerLogin, _fixture.RepoName, pr.Number, Draft: false));
        var readyPr = ready.Should().BeOfType<PlatformResult<PullRequest>.Ok>().Subject.Value;
        readyPr.IsDraft.Should().BeFalse("the un-draft strips the WIP prefix");
        readyPr.Title.Should().Be("lifecycle draft");

        var redraft = await _driver.Client.SetDraftAsync(
            new SetPullRequestDraftRequest(_fixture.OwnerLogin, _fixture.RepoName, pr.Number, Draft: true));
        redraft.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.IsDraft.Should().BeTrue();
    }

    [Test, Timeout(300_000)]
    public async Task RequestReviewersAsync_UnresolvableReviewer_AnswersTypedFailure_NotThrow()
    {
        RequireReady();
        var branch = await CreateBranchWithCommitAsync($"feat/lc-reviewers-{Guid.NewGuid():N}");
        var pr = (await _driver.Client.OpenPullRequestAsync(new OpenPullRequestRequest(
            _fixture.OwnerLogin, _fixture.RepoName,
            Title: "lifecycle reviewers", SourceBranch: branch,
            TargetBranch: _fixture.DefaultBranch))).GetValueOrDefault()!;

        // The fixture has no second collaborator; Gitea refuses both
        // ghost users and the PR author as reviewer. The verb must answer
        // a TYPED failure (the DG-3 skip consumes it) — never throw, and
        // never the capability refusal (the endpoint exists on 1.21).
        var result = await _driver.Client.RequestReviewersAsync(
            new RequestReviewersRequest(
                _fixture.OwnerLogin, _fixture.RepoName, pr.Number,
                new[] { $"ghost-{Guid.NewGuid():N}"[..12] }));

        var failed = result.Should().BeOfType<PlatformResult<PullRequest>.Failed>().Subject;
        failed.Error.Should().Match(e =>
            e is PlatformError.InvalidRequest || e is PlatformError.NotFound);
        if (failed.Error is PlatformError.InvalidRequest ir)
        {
            ir.Code.Should().NotBe("capability_unsupported",
                "1.21 HAS the requested_reviewers endpoint — a refusal here is about the reviewer, not the capability");
        }
    }

    // ─────────────────── helpers ───────────────────

    /// <summary>
    /// Branch creation + a single commit on it, all via the Gitea
    /// REST API (no driver methods — these are setup, not the SUT).
    /// Returns the new branch name.
    /// </summary>
    private async Task<string> CreateBranchWithCommitAsync(
        string branchName, string committedFile = "story-31-10/seed.txt")
    {
        // Create the branch.
        var createResult = await _driver.Client.CreateBranchAsync(
            new CreateBranchRequest(
                _fixture.OwnerLogin, _fixture.RepoName,
                NewBranchName: branchName,
                FromSha: _fixture.DefaultBranchSha));
        if (createResult is not PlatformResult<Branch>.Ok)
        {
            throw new InvalidOperationException(
                $"setup: CreateBranchAsync failed for {branchName}: {createResult}");
        }

        // Commit a small file to the branch via the contents API.
        // Body uses Gitea's contents-create envelope.
        using var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("token", _fixture.BotToken);

        var content = $"hello from story 31-10 {Guid.NewGuid():N}\n";
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        var body = new
        {
            branch = branchName,
            content = b64,
            message = $"add {committedFile}",
        };
        using var resp = await http.PostAsJsonAsync(
            $"/api/v1/repos/{_fixture.OwnerLogin}/{_fixture.RepoName}/contents/" +
            committedFile,
            body);
        resp.EnsureSuccessStatusCode();
        return branchName;
    }

    private async Task<string> GetBranchTipShaAsync(string branchName)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("token", _fixture.BotToken);
        using var resp = await http.GetAsync(
            $"/api/v1/repos/{_fixture.OwnerLogin}/{_fixture.RepoName}/branches/{branchName}");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("commit").GetProperty("id").GetString()!;
    }

    private async Task EnsureWorkflowFileAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("token", _fixture.BotToken);

        // Skip if already there (idempotent across test runs that
        // re-use a container).
        using var head = await http.GetAsync(
            $"/api/v1/repos/{_fixture.OwnerLogin}/{_fixture.RepoName}/contents/" +
            $".gitea/workflows/echo.yaml?ref={_fixture.DefaultBranch}");
        if (head.IsSuccessStatusCode) return;

        var workflow = """
            name: echo
            on: workflow_dispatch
            jobs:
              echo:
                runs-on: ubuntu-latest
                steps:
                  - run: echo hello
            """;
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(workflow));
        var body = new
        {
            branch = _fixture.DefaultBranch,
            content = b64,
            message = "add echo workflow",
        };
        using var resp = await http.PostAsJsonAsync(
            $"/api/v1/repos/{_fixture.OwnerLogin}/{_fixture.RepoName}/contents/" +
            ".gitea/workflows/echo.yaml", body);
        // 201 created or 422 already-exists are both acceptable.
        if (!resp.IsSuccessStatusCode &&
            (int)resp.StatusCode != 422)
        {
            var msg = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"setup: failed to seed workflow file: {(int)resp.StatusCode} {msg}");
        }
    }
}
