using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Null-object driver. Two intended uses:
/// <list type="number">
///   <item>Test fixtures that need an <see cref="IGitPlatformDriver"/>
///         instance without standing up a real driver.</item>
///   <item>Dev mode where no platform is configured — the registry
///         falls back to this so call sites that didn't check
///         <see cref="IGitPlatformDriver.Capabilities"/> still get a
///         deterministic response (<see cref="PlatformResult{T}.ServiceUnavailable"/>)
///         instead of a NullReferenceException.</item>
/// </list>
///
/// <para>All client methods return
/// <see cref="PlatformResult{T}.ServiceUnavailable"/>. The
/// <see cref="Actions"/> surface is null. Capabilities is the empty
/// set.</para>
/// </summary>
public sealed class NullGitPlatformDriver : IGitPlatformDriver
{
    /// <summary>
    /// Singleton — null driver has no per-instance state and no
    /// useful equality, so re-using one instance is fine.
    /// </summary>
    public static NullGitPlatformDriver Instance { get; } = new();

    public PlatformKind Kind { get; init; } = PlatformKind.GitHub;

    public IGitPlatformClient Client { get; } = new NullClient();

    public IGitPlatformActionsClient? Actions => null;

    public IReadOnlySet<PlatformCapability> Capabilities { get; } =
        new HashSet<PlatformCapability>();

    private sealed class NullClient : IGitPlatformClient
    {
        public Task<PlatformResult<Repo>> GetRepoAsync(
            string owner, string repoName, CancellationToken ct = default) =>
            Task.FromResult(PlatformResult<Repo>.FromServiceUnavailable());

        public Task<PlatformResult<IReadOnlyList<Branch>>> ListRepoBranchesAsync(
            string owner, string repoName, CancellationToken ct = default) =>
            Task.FromResult(PlatformResult<IReadOnlyList<Branch>>.FromServiceUnavailable());

        public Task<PlatformResult<byte[]>> GetFileContentAsync(
            GetFileContentRequest request, CancellationToken ct = default) =>
            Task.FromResult(PlatformResult<byte[]>.FromServiceUnavailable());

        public Task<PlatformResult<Branch>> CreateBranchAsync(
            CreateBranchRequest request, CancellationToken ct = default) =>
            Task.FromResult(PlatformResult<Branch>.FromServiceUnavailable());

        public Task<PlatformResult<PullRequest>> OpenPullRequestAsync(
            OpenPullRequestRequest request, CancellationToken ct = default) =>
            Task.FromResult(PlatformResult<PullRequest>.FromServiceUnavailable());

        public Task<PlatformResult<PullRequest>> GetPullRequestAsync(
            string owner, string repoName, string prNumber, CancellationToken ct = default) =>
            Task.FromResult(PlatformResult<PullRequest>.FromServiceUnavailable());

        public Task<PlatformResult<IReadOnlyList<PrFile>>> ListPullRequestFilesAsync(
            string owner, string repoName, string prNumber, CancellationToken ct = default) =>
            Task.FromResult(PlatformResult<IReadOnlyList<PrFile>>.FromServiceUnavailable());

        public Task<PlatformResult<IssueComment>> CreatePullRequestReviewCommentAsync(
            CreatePullRequestReviewCommentRequest request, CancellationToken ct = default) =>
            Task.FromResult(PlatformResult<IssueComment>.FromServiceUnavailable());

        public Task<PlatformResult<PullRequest>> MergePullRequestAsync(
            MergePullRequestRequest request, CancellationToken ct = default) =>
            Task.FromResult(PlatformResult<PullRequest>.FromServiceUnavailable());

        // Story 31-13 — the PR lifecycle verbs. The null driver never has the
        // PrLifecycle capability, so per the interface contract it returns
        // capability_unsupported (never throws).
        private static Task<PlatformResult<PullRequest>> PrLifecycleUnsupported() =>
            Task.FromResult(PlatformResult<PullRequest>.FromError(
                new PlatformError.InvalidRequest("capability_unsupported",
                    "the null git platform driver does not implement PR lifecycle verbs")));

        public Task<PlatformResult<PullRequest>> ClosePullRequestAsync(
            string owner, string repoName, string prNumber, CancellationToken ct = default) =>
            PrLifecycleUnsupported();

        public Task<PlatformResult<PullRequest>> ReopenPullRequestAsync(
            string owner, string repoName, string prNumber, CancellationToken ct = default) =>
            PrLifecycleUnsupported();

        public Task<PlatformResult<PullRequest>> RequestReviewersAsync(
            RequestReviewersRequest request, CancellationToken ct = default) =>
            PrLifecycleUnsupported();

        public Task<PlatformResult<PullRequest>> AddPullRequestLabelsAsync(
            AddPullRequestLabelsRequest request, CancellationToken ct = default) =>
            PrLifecycleUnsupported();

        public Task<PlatformResult<PullRequest>> RemovePullRequestLabelAsync(
            string owner, string repoName, string prNumber, string label, CancellationToken ct = default) =>
            PrLifecycleUnsupported();

        public Task<PlatformResult<PullRequest>> SetDraftAsync(
            SetPullRequestDraftRequest request, CancellationToken ct = default) =>
            PrLifecycleUnsupported();

        public Task<PlatformResult<IssueComment>> CreateIssueCommentAsync(
            string owner, string repoName, string issueOrPrNumber, string body,
            CancellationToken ct = default) =>
            Task.FromResult(PlatformResult<IssueComment>.FromServiceUnavailable());

        public Task<PlatformResult<WebhookRegistration>> RegisterWebhookAsync(
            RegisterWebhookRequest request, CancellationToken ct = default) =>
            Task.FromResult(PlatformResult<WebhookRegistration>.FromServiceUnavailable());

#pragma warning disable CS1998
        // No-op async iterator — empty repo list. The async modifier
        // gives us a valid IAsyncEnumerable without LINQ rewrites.
        public async IAsyncEnumerable<Repo> ListAccessibleReposAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998
    }
}
