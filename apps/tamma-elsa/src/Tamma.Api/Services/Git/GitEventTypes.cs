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
}

/// <summary>The credential-source LABEL surfaced on the audit event + response —
/// never the token itself (AC3).</summary>
public static class GitCredentialSources
{
    public const string Byok = "byok";
    public const string Platform = "platform";
}
