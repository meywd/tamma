using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitHub;

namespace Tamma.Platforms.IntegrationTests;

/// <summary>
/// Epic 31 P1 stage 2 — GitHub integration suite wired to the REAL
/// driver (<see cref="GitHubPlatformDriverFactory"/> → live REST/
/// GraphQL). GitHub ships no Docker image, so unlike the Gitea /
/// GitLab container fixtures this suite is <b>env-gated against the
/// live API</b> (the same skip-vs-fail philosophy as
/// <see cref="DockerAvailability"/>: missing credentials skip, they
/// don't fail):
///
/// <list type="bullet">
///   <item><c>TAMMA_GITHUB_IT_TOKEN</c> — a PAT with repo scope on the
///         test repository.</item>
///   <item><c>TAMMA_GITHUB_IT_REPO</c> — <c>owner/name</c> of a
///         disposable test repository the token can write to.</item>
///   <item><c>TAMMA_GITHUB_IT_BASE_URL</c> — optional; defaults to
///         <c>https://api.github.com</c> (set to a GHES
///         <c>/api/v3</c> root to certify GHES).</item>
/// </list>
///
/// <para>Read-only smoke coverage runs by default when the env vars
/// are present; mutation coverage (branch/PR lifecycle) is kept
/// behind <c>TAMMA_GITHUB_IT_ALLOW_WRITES=true</c> so a shared test
/// repo isn't littered by every run. Tagged Nightly like the GitLab
/// suite so per-PR CI (<c>Category!=Nightly</c>) skips it.</para>
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("Platforms")]
[Category("GitHub")]
[Category("Nightly")]
public class GitHubIntegrationTests
{
    private IGitPlatformDriver _driver = null!;
    private ServiceProvider _services = null!;
    private string _owner = null!;
    private string _repo = null!;

    private static (string Token, string Owner, string Repo, string BaseUrl) RequireEnvOrSkip()
    {
        var token = Environment.GetEnvironmentVariable("TAMMA_GITHUB_IT_TOKEN");
        var fullRepo = Environment.GetEnvironmentVariable("TAMMA_GITHUB_IT_REPO");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(fullRepo)
            || !fullRepo.Contains('/'))
        {
            Assert.Ignore(
                "GitHub live-API integration tests are env-gated: set " +
                "TAMMA_GITHUB_IT_TOKEN and TAMMA_GITHUB_IT_REPO (owner/name) to run.");
        }
        var baseUrl = Environment.GetEnvironmentVariable("TAMMA_GITHUB_IT_BASE_URL");
        var parts = fullRepo!.Split('/', 2);
        return (token!, parts[0], parts[1],
            string.IsNullOrWhiteSpace(baseUrl) ? "https://api.github.com" : baseUrl!);
    }

    private static void RequireWritesOrSkip()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("TAMMA_GITHUB_IT_ALLOW_WRITES"),
                "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Ignore(
                "Mutation coverage requires TAMMA_GITHUB_IT_ALLOW_WRITES=true " +
                "against a disposable test repository.");
        }
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var (token, owner, repo, baseUrl) = RequireEnvOrSkip();
        _owner = owner;
        _repo = repo;

        // Build the production driver via the registered factory —
        // mirrors what PlatformResolver does at runtime (the Gitea
        // suite's shape).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGitHubPlatformDriver();
        _services = services.BuildServiceProvider();

        var factory = _services.GetRequiredKeyedService<IGitPlatformDriverFactory>(
            PlatformKind.GitHub);
        var installation = new PlatformInstallation(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Kind: PlatformKind.GitHub,
            BaseUrl: baseUrl,
            InstallationExternalId: null);
        _driver = await factory.CreateAsync(installation, token);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _services?.Dispose();

    // ================================================================
    // Read-only smoke coverage
    // ================================================================

    [Test, Timeout(120_000)]
    public async Task GetRepo_returns_live_repo_metadata()
    {
        var result = await _driver.Client.GetRepoAsync(_owner, _repo);

        var repo = result.Should().BeOfType<PlatformResult<Repo>.Ok>().Subject.Value;
        repo.Name.Should().Be(_repo);
        repo.DefaultBranch.Should().NotBeNullOrEmpty();
    }

    [Test, Timeout(120_000)]
    public async Task ListRepoBranches_returns_at_least_the_default_branch()
    {
        var result = await _driver.Client.ListRepoBranchesAsync(_owner, _repo);

        var branches = result.Should()
            .BeOfType<PlatformResult<IReadOnlyList<Branch>>.Ok>().Subject.Value;
        branches.Should().NotBeEmpty();
        branches.Should().OnlyContain(b => !string.IsNullOrEmpty(b.Sha));
    }

    [Test, Timeout(120_000)]
    public async Task ListAccessibleRepos_yields_and_authenticates()
    {
        var seen = 0;
        await foreach (var repo in _driver.Client.ListAccessibleReposAsync())
        {
            repo.Name.Should().NotBeNullOrEmpty();
            if (++seen >= 3) break;
        }
        seen.Should().BeGreaterThan(0,
            "the probe surface must enumerate under a valid token");
    }

    [Test, Timeout(120_000)]
    public async Task ListCommits_on_default_branch_returns_history()
    {
        var repoResult = await _driver.Client.GetRepoAsync(_owner, _repo);
        var defaultBranch = repoResult.GetValueOrDefault()!.DefaultBranch;

        var result = await _driver.Client.ListCommitsAsync(
            new ListCommitsRequest(_owner, _repo, defaultBranch));

        result.Should().BeOfType<PlatformResult<IReadOnlyList<Commit>>.Ok>()
            .Which.Value.Should().NotBeEmpty();
    }

    [Test, Timeout(120_000)]
    public async Task Probe_fails_against_a_junk_token()
    {
        var factory = _services.GetRequiredKeyedService<IGitPlatformDriverFactory>(
            PlatformKind.GitHub);
        var junkDriver = await factory.CreateAsync(
            new PlatformInstallation(
                Guid.NewGuid(), Guid.NewGuid(), PlatformKind.GitHub,
                "https://api.github.com", null),
            "ghp_definitely_not_a_real_token");

        var act = async () =>
        {
            await foreach (var _ in junkDriver.Client.ListAccessibleReposAsync()) { break; }
        };

        await act.Should().ThrowAsync<GitHubPlatformApiException>(
            "P1 acceptance: the onboarding probe FAILS on a bad token");
    }

    // ================================================================
    // Mutation coverage — disposable-repo writes, opt-in
    // ================================================================

    [Test, Timeout(300_000)]
    public async Task Branch_PR_lifecycle_roundtrip()
    {
        RequireWritesOrSkip();

        var repoResult = await _driver.Client.GetRepoAsync(_owner, _repo);
        var defaultBranch = repoResult.GetValueOrDefault()!.DefaultBranch;
        var branches = await _driver.Client.ListRepoBranchesAsync(_owner, _repo);
        var baseSha = branches.GetValueOrDefault()!
            .First(b => b.Name == defaultBranch).Sha;

        var branchName = $"tamma-it/{Guid.NewGuid():N}";
        var created = await _driver.Client.CreateBranchAsync(
            new CreateBranchRequest(_owner, _repo, branchName, baseSha));
        created.IsOk.Should().BeTrue();

        // A PR needs a diff — without a commit on the branch GitHub
        // rejects creation (422). The full open→draft-toggle→label→
        // close roundtrip needs a content-write helper; wire it when
        // the nightly job gets its disposable-repo seeder (mirrors the
        // Gitea fixture's seeding step).
        var pr = await _driver.Client.OpenPullRequestAsync(
            new OpenPullRequestRequest(
                _owner, _repo, "tamma-it: no-diff probe", branchName, defaultBranch));
        (pr is PlatformResult<PullRequest>.Ok
         || (pr is PlatformResult<PullRequest>.Failed failed
             && failed.Error is PlatformError.InvalidRequest))
            .Should().BeTrue(
                "an identical-tree PR is either created or rejected with the typed 422 class");
    }
}
