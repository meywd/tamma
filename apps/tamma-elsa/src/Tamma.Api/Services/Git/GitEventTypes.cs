namespace Tamma.Api.Services.Git;

/// <summary>
/// Story 38-1 (AC7) — the terminal DCB event families the git-mediation
/// endpoints emit (exactly one per call). Naming mirrors the Story 32-5
/// <c>AGENT.RUN.*</c> convention (<c>AGGREGATE.ACTION.STATUS</c>). Payloads +
/// tags are KEY-FREE — they reference the repo + PR/issue numbers, never the
/// resolved git token or any Authorization header.
/// </summary>
public static class GitEventTypes
{
    // Operation labels (the `operation` tag + the {OPERATION} slug).
    public const string BranchCreateOperation = "branch_create";
    public const string PrOpenOperation = "pr_open";
    public const string PrMergeOperation = "pr_merge";
    public const string IssueUpdateOperation = "issue_update";
    public const string PrCommentsReadOperation = "pr_comments_read";

    // Story 43-12 — read a PR's details (base branch) before the merge gate decides
    // which per-target key (git.merge.dev|qa|main) applies. Same mediation plane.
    public const string PrDetailsReadOperation = "pr_details_read";

    // Story 38 (Phase 1) — the GitHub "extra ops" the engine's context / debug /
    // integration activities call on the composite today (commits + file-changes
    // reads and the standalone branch delete). Mediated here on the same
    // guard→token→platform→one-event plane as the git-platform ops above.
    public const string CommitsReadOperation = "commits_read";
    public const string FileChangesReadOperation = "file_changes_read";
    public const string BranchDeleteOperation = "branch_delete";

    // Epic 38 follow-up #21 — the deployment-pipeline release step (create a
    // GitHub release/tag for the shipped version). Same guard→token→platform→
    // one-event mediation plane as the git-platform ops above.
    public const string ReleaseCreateOperation = "release_create";

    // Story 31-13 — the 7 PR-lifecycle verbs (close, reopen, comment,
    // review-comment, request-reviewers, labels, set-draft), each on the same
    // guard→token→platform→exactly-one-terminal-event mediation plane.
    public const string PrCloseOperation = "pr_close";
    public const string PrReopenOperation = "pr_reopen";
    public const string PrCommentOperation = "pr_comment";
    public const string PrReviewCommentOperation = "pr_review_comment";
    public const string PrReviewersRequestOperation = "pr_reviewers_request";
    public const string PrLabelsUpdateOperation = "pr_labels_update";
    public const string PrDraftSetOperation = "pr_draft_set";

    public const string BranchCreatedSuccess = "GIT.BRANCH_CREATED.SUCCESS";
    public const string BranchCreatedFailed = "GIT.BRANCH_CREATED.FAILED";

    public const string PrOpenedSuccess = "GIT.PR_OPENED.SUCCESS";
    public const string PrOpenedFailed = "GIT.PR_OPENED.FAILED";

    public const string PrMergedSuccess = "GIT.PR_MERGED.SUCCESS";
    public const string PrMergeFailed = "GIT.PR_MERGE.FAILED";

    public const string IssueUpdatedSuccess = "GIT.ISSUE_UPDATED.SUCCESS";
    public const string IssueUpdatedFailed = "GIT.ISSUE_UPDATED.FAILED";

    public const string PrCommentsReadSuccess = "GIT.PR_COMMENTS_READ.SUCCESS";
    public const string PrCommentsReadFailed = "GIT.PR_COMMENTS_READ.FAILED";

    public const string PrDetailsReadSuccess = "GIT.PR_DETAILS_READ.SUCCESS";
    public const string PrDetailsReadFailed = "GIT.PR_DETAILS_READ.FAILED";

    public const string CommitsReadSuccess = "GIT.COMMITS_READ.SUCCESS";
    public const string CommitsReadFailed = "GIT.COMMITS_READ.FAILED";

    public const string FileChangesReadSuccess = "GIT.FILE_CHANGES_READ.SUCCESS";
    public const string FileChangesReadFailed = "GIT.FILE_CHANGES_READ.FAILED";

    public const string BranchDeletedSuccess = "GIT.BRANCH_DELETED.SUCCESS";
    public const string BranchDeletedFailed = "GIT.BRANCH_DELETED.FAILED";

    public const string ReleaseCreatedSuccess = "GIT.RELEASE_CREATED.SUCCESS";
    public const string ReleaseCreatedFailed = "GIT.RELEASE_CREATED.FAILED";

    // Story 31-13 — PR-lifecycle terminal events (AGGREGATE.ACTION.STATUS, key-free).
    public const string PrClosedSuccess = "GIT.PR_CLOSED.SUCCESS";
    public const string PrClosedFailed = "GIT.PR_CLOSED.FAILED";

    public const string PrReopenedSuccess = "GIT.PR_REOPENED.SUCCESS";
    public const string PrReopenedFailed = "GIT.PR_REOPENED.FAILED";

    public const string PrCommentedSuccess = "GIT.PR_COMMENTED.SUCCESS";
    public const string PrCommentedFailed = "GIT.PR_COMMENTED.FAILED";

    public const string PrReviewCommentedSuccess = "GIT.PR_REVIEW_COMMENTED.SUCCESS";
    public const string PrReviewCommentedFailed = "GIT.PR_REVIEW_COMMENTED.FAILED";

    public const string PrReviewersRequestedSuccess = "GIT.PR_REVIEWERS_REQUESTED.SUCCESS";
    public const string PrReviewersRequestedFailed = "GIT.PR_REVIEWERS_REQUESTED.FAILED";

    public const string PrLabelsUpdatedSuccess = "GIT.PR_LABELS_UPDATED.SUCCESS";
    public const string PrLabelsUpdatedFailed = "GIT.PR_LABELS_UPDATED.FAILED";

    public const string PrDraftSetSuccess = "GIT.PR_DRAFT_SET.SUCCESS";
    public const string PrDraftSetFailed = "GIT.PR_DRAFT_SET.FAILED";

    // ── Epic 31 P5 M2 — §4 degradation audit events (silent skips are
    //    forbidden; every alternative-step trip is on the record). These are
    //    ADDITIONAL audit rows next to the op's exactly-one TERMINAL event,
    //    per the execution plan's §5 ("event-type additions, not
    //    route/catalog changes").

    /// <summary>DG-3 — a reviewer request was skipped (capability
    /// unsupported / unresolvable reviewer / platform refusal) and the PR
    /// step proceeded without reviewers, labeled for a human.</summary>
    public const string PrReviewersSkipped = "GIT.PR_REVIEWERS.SKIPPED";

    /// <summary>DG-2 — a line-anchored review comment could not be
    /// anchored (capability unsupported or the platform rejected the
    /// anchor) and was downgraded to a plain PR comment carrying
    /// file:line in the body. The feedback is never dropped.</summary>
    public const string PrReviewCommentDowngraded = "GIT.PR_REVIEW_COMMENT.DOWNGRADED";

    /// <summary>DG-4 — the requested merge method was refused with the
    /// exact typed <c>merge_method_unsupported</c> code and the merge
    /// auto-fell back along the fixed order rebase→squash→merge.</summary>
    public const string PrMergeMethodFallback = "GIT.PR_MERGE.METHOD_FALLBACK";
}

/// <summary>
/// Story 38-1 (AC6) — the coarse, key-free failure taxonomy surfaced on the
/// wire so the ADL workflow can branch on the outcome exactly the way it does
/// today. Never a raw provider 5xx.
/// </summary>
public static class GitFailureCodes
{
    /// <summary>The tenant↔repo cross-tenant guard denied (AC2). Platform never called.</summary>
    public const string RepoNotAuthorized = "REPO_NOT_AUTHORIZED";

    /// <summary>An expected platform conflict (branch already exists, PR conflict).</summary>
    public const string GitConflict = "GIT_CONFLICT";

    /// <summary>The PR is not in a mergeable state.</summary>
    public const string NotMergeable = "NOT_MERGEABLE";

    /// <summary>The referenced branch / PR / issue was not found.</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>Any other expected platform failure (permission, rate-limit, transient).</summary>
    public const string PlatformError = "PLATFORM_ERROR";

    /// <summary>The per-tenant git token could not be resolved (fail-closed, AC3/AC6).</summary>
    public const string TokenUnavailable = "GIT_TOKEN_UNAVAILABLE";

    /// <summary>
    /// Epic 31 P2 (plan §4) — the resolved driver's platform cannot perform the
    /// requested verb. Surfaced FIRST-CLASS (exact code, lower-case, matching the
    /// driver contract's <c>PlatformError.InvalidRequest</c> code) so the
    /// workflow's capability check step / <c>Unsupported</c> safety-net outcome
    /// can branch on it. Never coarsened into <see cref="PlatformError"/>.
    /// </summary>
    public const string CapabilityUnsupported = "capability_unsupported";
}

/// <summary>The credential-source LABEL surfaced on the audit event + response —
/// never the token itself (AC3).</summary>
public static class GitCredentialSources
{
    public const string Byok = "byok";
    public const string Platform = "platform";
}
