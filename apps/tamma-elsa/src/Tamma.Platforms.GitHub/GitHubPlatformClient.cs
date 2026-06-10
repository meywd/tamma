using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Activities.AgentDispatch;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Story 31-3 — <see cref="IGitPlatformClient"/> backed by the
/// existing <see cref="IGitHubActionsClient"/> seam. The narrow
/// activities-side surface already covers the operations Tamma's
/// agent-dispatch loop needs (compare-refs ≈ branch listing, list-PRs
/// for head ≈ get PR by source-branch). For the operations the
/// existing seam does NOT cover (open PR, create branch, merge PR,
/// repo metadata, file content, register webhook, post issue / review
/// comments, list accessible repos), this driver returns
/// <see cref="PlatformResult{T}.ServiceUnavailable"/> with a debug
/// log — those flows go through <c>Tamma.Api</c>'s direct GitHub
/// services today and will be promoted to the abstraction by future
/// stories that extend <see cref="IGitHubActionsClient"/>.
///
/// <para>Why "wrap, don't rewrite": Story 31-3 keeps Octokit calls
/// inside <c>Tamma.Api</c> and <c>Tamma.Activities</c>. This driver
/// is a thin seam — it makes GitHub a peer to Gitea / GitLab /
/// Forgejo for purposes of <c>IPlatformResolver</c> registration and
/// capability discovery, without rewriting any GitHub call paths.</para>
/// </summary>
public sealed class GitHubPlatformClient : IGitPlatformClient
{
    private readonly IGitHubActionsClient _inner;
    private readonly string _host;
    private readonly ILogger _logger;

    /// <summary>
    /// Construct a driver-side platform client wrapping
    /// <paramref name="inner"/>. <paramref name="host"/> is the
    /// platform host without scheme (e.g. <c>github.com</c>) — used
    /// to populate <see cref="Repo.Host"/> when projecting.
    /// </summary>
    public GitHubPlatformClient(
        IGitHubActionsClient inner,
        string host,
        ILogger<GitHubPlatformClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _inner = inner;
        _host = host;
        _logger = logger ?? NullLogger<GitHubPlatformClient>.Instance;
    }

    /// <inheritdoc />
    public Task<PlatformResult<Repo>> GetRepoAsync(
        string owner, string repoName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        _logger.LogDebug(
            "GetRepoAsync not yet wired through the abstraction for {Owner}/{Repo}; falling back to ServiceUnavailable",
            owner, repoName);
        return Task.FromResult(PlatformResult<Repo>.FromServiceUnavailable());
    }

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyList<Branch>>> ListRepoBranchesAsync(
        string owner, string repoName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        // The existing seam only exposes Compare; we can extract the
        // base + head SHAs from a no-op compare (base==base) but the
        // result wouldn't enumerate all branches. Honest behaviour
        // here is to return ServiceUnavailable so callers know to
        // reach for an extended surface in a follow-up story.
        await Task.CompletedTask.ConfigureAwait(false);
        _logger.LogDebug(
            "ListRepoBranchesAsync not yet wired through the abstraction for {Owner}/{Repo}; returning ServiceUnavailable",
            owner, repoName);
        return PlatformResult<IReadOnlyList<Branch>>.FromServiceUnavailable();
    }

    /// <inheritdoc />
    public Task<PlatformResult<byte[]>> GetFileContentAsync(
        GetFileContentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogDebug(
            "GetFileContentAsync not yet wired through the abstraction for {Owner}/{Repo}; returning ServiceUnavailable",
            request.Owner, request.RepoName);
        return Task.FromResult(PlatformResult<byte[]>.FromServiceUnavailable());
    }

    /// <inheritdoc />
    public Task<PlatformResult<Branch>> CreateBranchAsync(
        CreateBranchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogDebug(
            "CreateBranchAsync not yet wired through the abstraction for {Owner}/{Repo}; returning ServiceUnavailable",
            request.Owner, request.RepoName);
        return Task.FromResult(PlatformResult<Branch>.FromServiceUnavailable());
    }

    /// <inheritdoc />
    public Task<PlatformResult<PullRequest>> OpenPullRequestAsync(
        OpenPullRequestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogDebug(
            "OpenPullRequestAsync not yet wired through the abstraction for {Owner}/{Repo}; returning ServiceUnavailable",
            request.Owner, request.RepoName);
        return Task.FromResult(PlatformResult<PullRequest>.FromServiceUnavailable());
    }

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> GetPullRequestAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(prNumber);
        // Best-effort: the existing seam exposes ListPullRequestsForHead
        // which is keyed by branch, not number. We can't satisfy a
        // by-number lookup from that surface without a wider seam, so
        // return ServiceUnavailable until a future story extends the
        // inner client. Awaiting a completed task keeps the method
        // shape async without warning noise.
        await Task.CompletedTask.ConfigureAwait(false);
        _logger.LogDebug(
            "GetPullRequestAsync not yet wired through the abstraction for {Owner}/{Repo}#{Number}; returning ServiceUnavailable",
            owner, repoName, prNumber);
        return PlatformResult<PullRequest>.FromServiceUnavailable();
    }

    /// <summary>
    /// Best-effort lookup of an open PR by source branch — backed by
    /// <see cref="IGitHubActionsClient.ListPullRequestsForHeadAsync"/>.
    /// Exposed as a non-interface helper so callers that have a head
    /// branch (rather than a PR number) can opt into the existing
    /// seam without the driver pretending it can do a by-number
    /// lookup.
    /// </summary>
    public async Task<PlatformResult<PullRequest>> FindPullRequestByHeadBranchAsync(
        string owner, string repoName, string headBranch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(headBranch);

        var prs = await _inner.ListPullRequestsForHeadAsync(owner, repoName, headBranch, ct)
            .ConfigureAwait(false);
        if (prs.Count == 0)
        {
            return PlatformResult<PullRequest>.FromError(new PlatformError.NotFound());
        }
        return PlatformResult<PullRequest>.FromOk(
            GitHubModelMapper.ToPullRequest(prs[0], headBranch));
    }

    /// <inheritdoc />
    public Task<PlatformResult<IReadOnlyList<PrFile>>> ListPullRequestFilesAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(prNumber);
        _logger.LogDebug(
            "ListPullRequestFilesAsync not yet wired through the abstraction for {Owner}/{Repo}#{Number}; returning ServiceUnavailable",
            owner, repoName, prNumber);
        return Task.FromResult(PlatformResult<IReadOnlyList<PrFile>>.FromServiceUnavailable());
    }

    /// <inheritdoc />
    public Task<PlatformResult<IssueComment>> CreatePullRequestReviewCommentAsync(
        CreatePullRequestReviewCommentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogDebug(
            "CreatePullRequestReviewCommentAsync not yet wired through the abstraction for {Owner}/{Repo}; returning ServiceUnavailable",
            request.Owner, request.RepoName);
        return Task.FromResult(PlatformResult<IssueComment>.FromServiceUnavailable());
    }

    /// <inheritdoc />
    public Task<PlatformResult<PullRequest>> MergePullRequestAsync(
        MergePullRequestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogDebug(
            "MergePullRequestAsync not yet wired through the abstraction for {Owner}/{Repo}#{Number}; returning ServiceUnavailable",
            request.Owner, request.RepoName, request.PrNumber);
        return Task.FromResult(PlatformResult<PullRequest>.FromServiceUnavailable());
    }

    /// <inheritdoc />
    public Task<PlatformResult<IssueComment>> CreateIssueCommentAsync(
        string owner, string repoName, string issueOrPrNumber, string body,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(issueOrPrNumber);
        ArgumentNullException.ThrowIfNull(body);
        _logger.LogDebug(
            "CreateIssueCommentAsync not yet wired through the abstraction for {Owner}/{Repo}#{Number}; returning ServiceUnavailable",
            owner, repoName, issueOrPrNumber);
        return Task.FromResult(PlatformResult<IssueComment>.FromServiceUnavailable());
    }

    /// <inheritdoc />
    public Task<PlatformResult<WebhookRegistration>> RegisterWebhookAsync(
        RegisterWebhookRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogDebug(
            "RegisterWebhookAsync not yet wired through the abstraction for {Owner}/{Repo}; returning ServiceUnavailable",
            request.Owner, request.RepoName);
        return Task.FromResult(PlatformResult<WebhookRegistration>.FromServiceUnavailable());
    }

    /// <inheritdoc />
#pragma warning disable CS1998 // async method without await
    public async IAsyncEnumerable<Repo> ListAccessibleReposAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
#pragma warning restore CS1998
    {
        // GitHub's "accessible repos" listing lives on the App-level
        // surface (IGitHubAppClient.ListInstallationReposAsync) and is
        // out of scope for the wave-B finishing touch — onboarding /
        // install flows already use that surface directly. Yield no
        // results so the abstraction is honest.
        ct.ThrowIfCancellationRequested();
        _logger.LogDebug(
            "ListAccessibleReposAsync not yet wired through the abstraction; yielding empty sequence");
        yield break;
    }

    /// <summary>
    /// Hostname (e.g. <c>github.com</c>) the driver was constructed
    /// for — useful for diagnostics + tests.
    /// </summary>
    public string Host => _host;
}
