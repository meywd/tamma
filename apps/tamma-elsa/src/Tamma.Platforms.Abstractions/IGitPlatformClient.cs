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

    // ── Epic 31 P2 — core verbs the mediation swap needs that were
    //    missing from the P1 stage-1 additions: single-branch read
    //    (existence + tip SHA), branch delete, open-PR-for-branch
    //    lookup (the idempotent create-or-update dance), and PR
    //    title/body update. Core verbs like GetRepo/CreateBranch —
    //    every wired driver implements them for real; the null seam
    //    answers the bare ServiceUnavailable stub. ──

    /// <summary>
    /// Read a single branch. <see cref="PlatformError.NotFound"/> when
    /// the branch does not exist — callers use this as the existence
    /// probe (the live path's <c>BranchExistsAsync</c> shape).
    /// </summary>
    Task<PlatformResult<Branch>> GetBranchAsync(
        string owner, string repoName, string branchName, CancellationToken ct = default);

    /// <summary>
    /// Delete a branch ref. Returns true on success. Deleting an
    /// absent branch answers <see cref="PlatformError.NotFound"/> (the
    /// caller decides whether that is a failure).
    /// </summary>
    Task<PlatformResult<bool>> DeleteBranchAsync(
        string owner, string repoName, string branchName, CancellationToken ct = default);

    /// <summary>
    /// List the OPEN pull requests whose source branch is
    /// <paramref name="sourceBranch"/> and target branch is
    /// <paramref name="targetBranch"/> (0 or 1 entries on every
    /// platform that forbids duplicate open PRs per branch pair).
    /// Powers the create-PR idempotency lookup.
    /// </summary>
    Task<PlatformResult<IReadOnlyList<PullRequest>>> ListOpenPullRequestsForBranchAsync(
        string owner, string repoName, string sourceBranch, string targetBranch,
        CancellationToken ct = default);

    /// <summary>
    /// Update an existing PR's title and/or body (null = leave as-is).
    /// Returns the updated PR.
    /// </summary>
    Task<PlatformResult<PullRequest>> UpdatePullRequestAsync(
        UpdatePullRequestRequest request, CancellationToken ct = default);

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

    // ── Epic 31 P1 (stage 1) — the verbs the autonomous loop performs
    //    through the live GitHub path (GitHubIntegrationService) that were
    //    missing from the abstraction: issue close + labels, release
    //    create, PR review-comment listing, commits, and branch
    //    file-changes. Added NOW, across all drivers, so P2's mediation
    //    swap doesn't churn this interface mid-flight. Same contract as
    //    the 31-13 lifecycle verbs: a driver without the corresponding
    //    capability (IssueLifecycle / Releases / PrReviewCommentRead /
    //    CommitReads) MUST return PlatformError.InvalidRequest with code
    //    "capability_unsupported" — never throw. Every driver answers
    //    unsupported today; GitHub implements them in P1 stage 2. ──

    /// <summary>
    /// Close an issue, optionally posting a closing comment first
    /// (returns the updated issue). Capability:
    /// <see cref="PlatformCapability.IssueLifecycle"/>.
    /// </summary>
    Task<PlatformResult<Issue>> CloseIssueAsync(
        string owner, string repoName, string issueNumber, string? comment = null,
        CancellationToken ct = default);

    /// <summary>
    /// Add labels to an issue; returns the issue's full label set after
    /// the add. Capability: <see cref="PlatformCapability.IssueLifecycle"/>.
    /// </summary>
    Task<PlatformResult<IReadOnlyList<string>>> AddIssueLabelsAsync(
        AddIssueLabelsRequest request, CancellationToken ct = default);

    /// <summary>
    /// Remove a single label from an issue; returns the remaining label
    /// set. Removing an absent label is idempotent success. Capability:
    /// <see cref="PlatformCapability.IssueLifecycle"/>.
    /// </summary>
    Task<PlatformResult<IReadOnlyList<string>>> RemoveIssueLabelAsync(
        string owner, string repoName, string issueNumber, string label,
        CancellationToken ct = default);

    /// <summary>
    /// Create a release for a tag. Capability:
    /// <see cref="PlatformCapability.Releases"/>.
    /// </summary>
    Task<PlatformResult<Release>> CreateReleaseAsync(
        CreateReleaseRequest request, CancellationToken ct = default);

    /// <summary>
    /// List the file/line-anchored review comments on a PR (the read
    /// side of <see cref="CreatePullRequestReviewCommentAsync"/>).
    /// Capability: <see cref="PlatformCapability.PrReviewCommentRead"/>.
    /// </summary>
    Task<PlatformResult<IReadOnlyList<PullRequestReviewComment>>> ListPullRequestReviewCommentsAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default);

    /// <summary>
    /// List recent commits on a ref, newest first. Capability:
    /// <see cref="PlatformCapability.CommitReads"/>.
    /// </summary>
    Task<PlatformResult<IReadOnlyList<Commit>>> ListCommitsAsync(
        ListCommitsRequest request, CancellationToken ct = default);

    /// <summary>
    /// List the files changed on a branch relative to a base ref (the
    /// repo's default branch when the request leaves it null).
    /// Capability: <see cref="PlatformCapability.CommitReads"/>.
    /// </summary>
    Task<PlatformResult<IReadOnlyList<PrFile>>> ListBranchFileChangesAsync(
        ListBranchFileChangesRequest request, CancellationToken ct = default);

    // ── Epic 31 P3 (seam 5) — the engine-callback verbs the loop's
    //    work selection + triage flow ride on: issue listing, issue
    //    creation, and the security-alert read. Same contract as the
    //    other capability-gated verbs: a driver that cannot answer
    //    returns PlatformError.InvalidRequest with code
    //    "capability_unsupported" — never throws. ──

    /// <summary>
    /// List issues (excluding pull requests) matching the filter. The
    /// work-item selection read. Capability:
    /// <see cref="PlatformCapability.IssueLifecycle"/>.
    /// </summary>
    Task<PlatformResult<IReadOnlyList<Issue>>> ListIssuesAsync(
        ListIssuesRequest request, CancellationToken ct = default);

    /// <summary>
    /// Create a new issue; returns the created issue with its REAL
    /// platform <see cref="Issue.HtmlUrl"/> (no caller may fabricate a
    /// github.com URL). Capability:
    /// <see cref="PlatformCapability.IssueLifecycle"/>.
    /// </summary>
    Task<PlatformResult<Issue>> CreateIssueAsync(
        CreateIssueRequest request, CancellationToken ct = default);

    /// <summary>
    /// List open security alerts (dependency + static analysis) as
    /// platform-native raw JSON. Platforms without a security-alert
    /// surface answer <c>capability_unsupported</c>; a platform WITH
    /// the surface but a repo where a scanner is disabled returns an
    /// empty list for that scanner (never a failure).
    /// </summary>
    Task<PlatformResult<SecurityAlerts>> ListSecurityAlertsAsync(
        string owner, string repoName, string alertType, CancellationToken ct = default);

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
