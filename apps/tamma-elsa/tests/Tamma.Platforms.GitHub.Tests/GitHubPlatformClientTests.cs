using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitHub;

namespace Tamma.Platforms.GitHub.Tests;

/// <summary>
/// Story 31-3 — tests for the source-host surface.
/// <see cref="GitHubPlatformClient"/> wraps the narrow
/// <see cref="IGitHubActionsClient"/> seam; operations not yet
/// covered by the inner seam return
/// <see cref="PlatformResult{T}.ServiceUnavailable"/>. The
/// <see cref="GitHubPlatformClient.FindPullRequestByHeadBranchAsync"/>
/// helper exposes the one operation that IS backed today (PR by
/// head-branch lookup).
/// </summary>
[TestFixture]
public sealed class GitHubPlatformClientTests
{
    private static GitHubPlatformClient BuildClient(IGitHubActionsClient inner, string host = "github.com") =>
        new(inner, host);

    [Test]
    public void Constructor_rejects_null_inner_client()
    {
        Action act = () => new GitHubPlatformClient(null!, "github.com");
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_rejects_blank_host()
    {
        var inner = Mock.Of<IGitHubActionsClient>();
        Action act = () => new GitHubPlatformClient(inner, host: "  ");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Host_exposes_constructor_value()
    {
        var inner = Mock.Of<IGitHubActionsClient>();
        var client = BuildClient(inner, host: "github.acme.corp");
        client.Host.Should().Be("github.acme.corp");
    }

    // ── ServiceUnavailable surface (operations not yet wired) ──────

    [Test]
    public async Task GetRepoAsync_returns_service_unavailable()
    {
        var client = BuildClient(Mock.Of<IGitHubActionsClient>());
        var result = await client.GetRepoAsync("acme", "repo");
        result.Should().BeOfType<PlatformResult<Repo>.ServiceUnavailable>();
    }

    [Test]
    public async Task ListRepoBranchesAsync_returns_service_unavailable()
    {
        var client = BuildClient(Mock.Of<IGitHubActionsClient>());
        var result = await client.ListRepoBranchesAsync("acme", "repo");
        result.Should().BeOfType<PlatformResult<IReadOnlyList<Branch>>.ServiceUnavailable>();
    }

    [Test]
    public async Task GetFileContentAsync_returns_service_unavailable()
    {
        var client = BuildClient(Mock.Of<IGitHubActionsClient>());
        var result = await client.GetFileContentAsync(
            new GetFileContentRequest("acme", "repo", "README.md", "main"));
        result.Should().BeOfType<PlatformResult<byte[]>.ServiceUnavailable>();
    }

    [Test]
    public async Task CreateBranchAsync_returns_service_unavailable()
    {
        var client = BuildClient(Mock.Of<IGitHubActionsClient>());
        var result = await client.CreateBranchAsync(
            new CreateBranchRequest("acme", "repo", "feat/new", "deadbeef"));
        result.Should().BeOfType<PlatformResult<Branch>.ServiceUnavailable>();
    }

    [Test]
    public async Task OpenPullRequestAsync_returns_service_unavailable()
    {
        var client = BuildClient(Mock.Of<IGitHubActionsClient>());
        var result = await client.OpenPullRequestAsync(
            new OpenPullRequestRequest("acme", "repo", "feat: x", "feat/new", "main"));
        result.Should().BeOfType<PlatformResult<PullRequest>.ServiceUnavailable>();
    }

    [Test]
    public async Task GetPullRequestAsync_returns_service_unavailable()
    {
        var client = BuildClient(Mock.Of<IGitHubActionsClient>());
        var result = await client.GetPullRequestAsync("acme", "repo", "42");
        result.Should().BeOfType<PlatformResult<PullRequest>.ServiceUnavailable>();
    }

    [Test]
    public async Task ListPullRequestFilesAsync_returns_service_unavailable()
    {
        var client = BuildClient(Mock.Of<IGitHubActionsClient>());
        var result = await client.ListPullRequestFilesAsync("acme", "repo", "42");
        result.Should().BeOfType<PlatformResult<IReadOnlyList<PrFile>>.ServiceUnavailable>();
    }

    [Test]
    public async Task CreatePullRequestReviewCommentAsync_returns_service_unavailable()
    {
        var client = BuildClient(Mock.Of<IGitHubActionsClient>());
        var result = await client.CreatePullRequestReviewCommentAsync(
            new CreatePullRequestReviewCommentRequest("acme", "repo", "42", "src/x.cs", 7, "lgtm", "sha"));
        result.Should().BeOfType<PlatformResult<IssueComment>.ServiceUnavailable>();
    }

    [Test]
    public async Task MergePullRequestAsync_returns_service_unavailable()
    {
        var client = BuildClient(Mock.Of<IGitHubActionsClient>());
        var result = await client.MergePullRequestAsync(
            new MergePullRequestRequest("acme", "repo", "42", MergeMethod.Squash));
        result.Should().BeOfType<PlatformResult<PullRequest>.ServiceUnavailable>();
    }

    [Test]
    public async Task CreateIssueCommentAsync_returns_service_unavailable()
    {
        var client = BuildClient(Mock.Of<IGitHubActionsClient>());
        var result = await client.CreateIssueCommentAsync("acme", "repo", "42", "hi");
        result.Should().BeOfType<PlatformResult<IssueComment>.ServiceUnavailable>();
    }

    [Test]
    public async Task RegisterWebhookAsync_returns_service_unavailable()
    {
        var client = BuildClient(Mock.Of<IGitHubActionsClient>());
        var result = await client.RegisterWebhookAsync(
            new RegisterWebhookRequest(
                "acme", "repo",
                "https://tamma.example.com/webhooks/github",
                new[] { "push" },
                "shhh"));
        result.Should().BeOfType<PlatformResult<WebhookRegistration>.ServiceUnavailable>();
    }

    [Test]
    public async Task ListAccessibleReposAsync_yields_no_results()
    {
        var client = BuildClient(Mock.Of<IGitHubActionsClient>());
        var repos = new List<Repo>();
        await foreach (var repo in client.ListAccessibleReposAsync())
        {
            repos.Add(repo);
        }
        repos.Should().BeEmpty();
    }

    // ── FindPullRequestByHeadBranchAsync — backed by inner seam ────

    [Test]
    public async Task FindPullRequestByHeadBranchAsync_returns_not_found_when_no_PRs_match()
    {
        var inner = new Mock<IGitHubActionsClient>();
        inner.Setup(x => x.ListPullRequestsForHeadAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PullRequestSummary>());

        var client = BuildClient(inner.Object);

        var result = await client.FindPullRequestByHeadBranchAsync("acme", "repo", "feat/x");

        result.Should().BeOfType<PlatformResult<PullRequest>.Failed>();
        ((PlatformResult<PullRequest>.Failed)result).Error.Should().BeOfType<PlatformError.NotFound>();
    }

    [Test]
    public async Task FindPullRequestByHeadBranchAsync_translates_first_PR()
    {
        var summary = new PullRequestSummary(
            Number: 42,
            Title: "feat: gizmo",
            Body: "body",
            HtmlUrl: "https://github.com/acme/repo/pull/42",
            HeadSha: "sha",
            ChangedFiles: 3);
        var inner = new Mock<IGitHubActionsClient>();
        inner.Setup(x => x.ListPullRequestsForHeadAsync(
                "acme", "repo", "feat/gizmo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { summary });

        var client = BuildClient(inner.Object);

        var result = await client.FindPullRequestByHeadBranchAsync("acme", "repo", "feat/gizmo");

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
        var pr = ((PlatformResult<PullRequest>.Ok)result).Value;
        pr.Number.Should().Be("42");
        pr.SourceBranch.Should().Be("feat/gizmo");
    }
}
