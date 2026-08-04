namespace Tamma.Api.Services.Git;

/// <summary>
/// Story 38-1 — the managed git execution layer behind the
/// <c>/api/v1/git/{repo}/...</c> endpoints. Composes the rule-1 sequence ENTIRELY
/// inside <c>Tamma.Api</c> (the only place the git token lives): cross-tenant
/// guard → per-tenant token resolution (BYOK→platform) → platform call with the
/// RESOLVED token → exactly-one terminal DCB audit event. ALWAYS returns a typed,
/// key-free <see cref="GitMediationResult"/> — a failure never throws a raw 5xx.
/// </summary>
public interface IGitMediationService
{
    Task<GitMediationResult> CreateBranchAsync(Guid? tenantId, string repo, CreateBranchRequest body, CancellationToken ct = default);
    Task<GitMediationResult> CreatePullRequestAsync(Guid? tenantId, string repo, CreatePrRequest body, CancellationToken ct = default);
    Task<GitMediationResult> MergePullRequestAsync(Guid? tenantId, string repo, int prNumber, MergePrRequest body, CancellationToken ct = default);
    Task<GitMediationResult> UpdateIssueAsync(Guid? tenantId, string repo, int issueNumber, UpdateIssueRequest body, CancellationToken ct = default);
    Task<GitMediationResult> GetPullRequestCommentsAsync(Guid? tenantId, string repo, int prNumber, string correlationId, CancellationToken ct = default);

    /// <summary>
    /// Story 43-12 — read a PR's details (its base/target branch) so the merge gate
    /// can resolve the per-target catalog key (<c>git.merge.dev|qa|main</c>) BEFORE
    /// deciding. Read op — guard→token→platform→one event. On any failure the
    /// result's <see cref="GitMediationResult.TargetBranch"/> is null and the caller
    /// (the selector) fails closed to <c>git.merge.main</c>.
    /// </summary>
    Task<GitMediationResult> GetPullRequestAsync(Guid? tenantId, string repo, int prNumber, string correlationId, CancellationToken ct = default);

    // Story 38 (Phase 1) — GitHub "extra ops" the engine's context/debug/integration
    // activities call on the composite today, mediated on the same plane.

    /// <summary>Read recent commits on <paramref name="branch"/> (optionally
    /// <paramref name="since"/>). Read op — guard→token→platform→one event.</summary>
    Task<GitMediationResult> GetCommitsAsync(Guid? tenantId, string repo, string branch, DateTime? since, string correlationId, CancellationToken ct = default);

    /// <summary>Read the file changes on <paramref name="branch"/> relative to the
    /// repo default. Read op — guard→token→platform→one event.</summary>
    Task<GitMediationResult> GetFileChangesAsync(Guid? tenantId, string repo, string branch, string correlationId, CancellationToken ct = default);

    /// <summary>Delete <paramref name="branchName"/> (the standalone delete the
    /// composite exposes, distinct from the verified post-merge delete). Write op.</summary>
    Task<GitMediationResult> DeleteBranchAsync(Guid? tenantId, string repo, string branchName, string correlationId, CancellationToken ct = default);

    /// <summary>
    /// Epic 38 follow-up #21 — create a GitHub release/tag for the shipped version
    /// (the deployment-pipeline release step). Write op —
    /// guard→token→platform→one-event.
    /// </summary>
    Task<GitMediationResult> CreateReleaseAsync(Guid? tenantId, string repo, CreateReleaseRequest body, CancellationToken ct = default);

    // ============================================================
    // Story 31-13 — the 7 PR-lifecycle verbs, each on the same
    // guard→token→platform→exactly-one-terminal-event plane.
    // ============================================================

    /// <summary>Close an open PR (PATCH state=closed). Reversible via reopen. Write op.</summary>
    Task<GitMediationResult> ClosePullRequestAsync(Guid? tenantId, string repo, int prNumber, ClosePrRequest body, CancellationToken ct = default);

    /// <summary>Reopen a closed PR (PATCH state=open). The inverse of close. Write op.</summary>
    Task<GitMediationResult> ReopenPullRequestAsync(Guid? tenantId, string repo, int prNumber, ReopenPrRequest body, CancellationToken ct = default);

    /// <summary>Post an issue-style comment on the PR (a PR IS an issue on GitHub). Write op.</summary>
    Task<GitMediationResult> CommentOnPullRequestAsync(Guid? tenantId, string repo, int prNumber, PrCommentRequest body, CancellationToken ct = default);

    /// <summary>Post a review comment anchored to a diff line. Write op.</summary>
    Task<GitMediationResult> ReviewCommentOnPullRequestAsync(Guid? tenantId, string repo, int prNumber, PrReviewCommentRequest body, CancellationToken ct = default);

    /// <summary>Request reviewers for the PR (failure surfaced, not swallowed). Write op.</summary>
    Task<GitMediationResult> RequestPullRequestReviewersAsync(Guid? tenantId, string repo, int prNumber, PrReviewersRequest body, CancellationToken ct = default);

    /// <summary>Add and/or remove PR labels in ONE op (D2 — first failure is a loud typed failure). Write op.</summary>
    Task<GitMediationResult> UpdatePullRequestLabelsAsync(Guid? tenantId, string repo, int prNumber, PrLabelsRequest body, CancellationToken ct = default);

    /// <summary>Toggle the PR's draft state (GraphQL-backed on GitHub). Write op.</summary>
    Task<GitMediationResult> SetPullRequestDraftAsync(Guid? tenantId, string repo, int prNumber, PrDraftRequest body, CancellationToken ct = default);
}
