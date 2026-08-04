using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-1 AC1 — the "source-host" surface every git platform
/// driver implements: repos, PRs/MRs, issues, branches, file
/// content, webhooks. CI dispatch lives on the sister interface
/// <see cref="IGitPlatformActionsClient"/> because pure-git forges
/// (or platforms where Tamma only reads) may not need it.
///
/// <para>All methods return <see cref="PlatformResult{T}"/> — drivers
/// MUST NOT throw on platform errors (404, rate-limit, auth).
/// Drivers MAY throw on programming errors (null arg, malformed
/// input) — those are bugs, not operator-actionable failures.</para>
///
/// <para>Idempotency note (impl-plan §locked-decisions): callers must
/// treat <see cref="OpenPullRequestAsync"/> as
/// at-least-once. Drivers SHOULD detect existing PRs with the same
/// (sourceBranch, targetBranch) pair and return the existing one
/// rather than failing — but this is best-effort. Callers that need
/// strict idempotency should use a workflow-level idempotency key.</para>
///
/// <para>Auth note: a driver instance is constructed for a specific
/// <see cref="Models.PlatformInstallation"/>. The Story 31-2 registry
/// hands callers a driver bound to a single tenant + installation
/// pair — the interface itself is auth-agnostic.</para>
/// </summary>
public interface IGitPlatformClient
{
    /// <summary>
    /// Fetch a repo's metadata.
    /// </summary>
    Task<PlatformResult<Repo>> GetRepoAsync(
        string owner, string repoName, CancellationToken ct = default);

    /// <summary>
    /// List branches for a repo. Pages internally.
    /// </summary>
    Task<PlatformResult<IReadOnlyList<Branch>>> ListRepoBranchesAsync(
        string owner, string repoName, CancellationToken ct = default);

    /// <summary>
    /// Read the raw content of a file at a specific ref.
    /// Returns the bytes verbatim — driver does no decoding.
    /// </summary>
    Task<PlatformResult<byte[]>> GetFileContentAsync(
        GetFileContentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Create a new branch from an existing SHA.
    /// </summary>
    Task<PlatformResult<Branch>> CreateBranchAsync(
        CreateBranchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Open a new PR. See type-level remarks on idempotency.
    /// </summary>
    Task<PlatformResult<PullRequest>> OpenPullRequestAsync(
        OpenPullRequestRequest request, CancellationToken ct = default);

    /// <summary>
    /// Get a single PR by number.
    /// </summary>
    Task<PlatformResult<PullRequest>> GetPullRequestAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default);

    /// <summary>
    /// List the file diffs in a PR.
    /// </summary>
    Task<PlatformResult<IReadOnlyList<PrFile>>> ListPullRequestFilesAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default);

    /// <summary>
    /// Post a file/line-anchored review comment on a PR. Drivers that
    /// don't support file-level review (capability flag absent) MUST
    /// return <see cref="PlatformError.InvalidRequest"/> with code
    /// <c>"capability_unsupported"</c> rather than silently posting
    /// to the issue thread.
    /// </summary>
    Task<PlatformResult<IssueComment>> CreatePullRequestReviewCommentAsync(
        CreatePullRequestReviewCommentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Merge a PR using the requested merge method.
    /// </summary>
    Task<PlatformResult<PullRequest>> MergePullRequestAsync(
        MergePullRequestRequest request, CancellationToken ct = default);

    // ── Story 31-13 — the full PR lifecycle. Drivers without the
    //    PrLifecycle capability MUST return PlatformError.InvalidRequest
    //    with code "capability_unsupported" (never throw — the no-throw
    //    contract above). Close↔reopen invert; the rest are editable
    //    on-platform. ──

    /// <summary>Close an open PR (returns the updated PR).</summary>
    Task<PlatformResult<PullRequest>> ClosePullRequestAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default);

    /// <summary>Reopen a closed PR (returns the updated PR).</summary>
    Task<PlatformResult<PullRequest>> ReopenPullRequestAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default);

    /// <summary>Request individual and/or team reviewers on a PR.</summary>
    Task<PlatformResult<PullRequest>> RequestReviewersAsync(
        RequestReviewersRequest request, CancellationToken ct = default);

    /// <summary>Add labels to a PR (labels live on the issue side of a PR).</summary>
    Task<PlatformResult<PullRequest>> AddPullRequestLabelsAsync(
        AddPullRequestLabelsRequest request, CancellationToken ct = default);

    /// <summary>Remove a single label from a PR.</summary>
    Task<PlatformResult<PullRequest>> RemovePullRequestLabelAsync(
        string owner, string repoName, string prNumber, string label, CancellationToken ct = default);

    /// <summary>Toggle a PR between draft and ready-for-review.</summary>
    Task<PlatformResult<PullRequest>> SetDraftAsync(
        SetPullRequestDraftRequest request, CancellationToken ct = default);

    /// <summary>
    /// Post a top-level comment on an issue or PR.
    /// </summary>
    Task<PlatformResult<IssueComment>> CreateIssueCommentAsync(
        string owner, string repoName, string issueOrPrNumber, string body,
        CancellationToken ct = default);

    /// <summary>
    /// Register an inbound webhook so the platform calls back to
    /// Tamma. Story 31-7 ships the receiver that consumes these.
    /// </summary>
    Task<PlatformResult<WebhookRegistration>> RegisterWebhookAsync(
        RegisterWebhookRequest request, CancellationToken ct = default);

    /// <summary>
    /// Story 31-9 AC7 — onboarding "pick a repo" UI consumes this
    /// without knowing repo names ahead of time.
    ///
    /// <para>Returned as <see cref="IAsyncEnumerable{Repo}"/> because
    /// platforms paginate differently (GitHub link header, GitLab
    /// link header + Keyset, Gitea page param). Drivers handle
    /// pagination internally; callers can <c>await foreach</c> until
    /// they have enough.</para>
    ///
    /// <para>Drivers without this capability (capability flag
    /// absent) MAY return an empty sequence — callers should check
    /// <see cref="IGitPlatformDriver.Capabilities"/> first to render
    /// a useful error rather than silently showing zero repos.</para>
    /// </summary>
    IAsyncEnumerable<Repo> ListAccessibleReposAsync(CancellationToken ct = default);
}
