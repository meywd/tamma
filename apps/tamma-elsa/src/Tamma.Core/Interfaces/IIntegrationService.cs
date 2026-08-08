namespace Tamma.Core.Interfaces;

// ============================================
// Standardized result type for integration operations
// ============================================

/// <summary>
/// Standardized result for integration operations.
/// Success/Data for expected outcomes; throw only for unexpected errors.
/// </summary>
public record IntegrationResult<T>(bool Success, T? Data, string? Error = null)
{
    public static IntegrationResult<T> Ok(T data) => new(true, data);
    public static IntegrationResult<T> Fail(string error) => new(false, default, error);
}

// ============================================
// Focused integration interfaces (new, use IntegrationResult<T>)
// ============================================

/// <summary>
/// Slack messaging operations.
/// </summary>
public interface ISlackIntegrationService
{
    /// <summary>Send a message via Slack</summary>
    Task<IntegrationResult<bool>> SendSlackMessageAsync(string channel, string message);

    /// <summary>Send a direct message to a user via Slack</summary>
    Task<IntegrationResult<bool>> SendSlackDirectMessageAsync(string userId, string message);
}

// Epic 31 P3 (4/4): IGitHubIntegrationService was DELETED with its
// implementation (GitHubIntegrationService) — the production git path is the
// platform driver plane (IGitPlatformClient) behind the /api/v1/git mediation
// endpoints. The GitHub* wire DTOs below remain: they are the mediation
// planes' response shapes, not a client surface.

/// <summary>
/// JIRA ticket management operations.
/// </summary>
public interface IJiraIntegrationService
{
    /// <summary>Create or update a JIRA ticket</summary>
    Task<IntegrationResult<JiraTicketResult>> UpdateJiraTicketAsync(string ticketId, JiraTicketUpdate update);

    /// <summary>Get JIRA ticket details</summary>
    Task<IntegrationResult<JiraTicket?>> GetJiraTicketAsync(string ticketId);
}

// Epic 31 P3 (4/4): ICIIntegrationService was DELETED with its implementation
// (CIIntegrationService / CiClientFactory) — CI mediation rides the resolved
// driver's IGitPlatformActionsClient. TestRunResult / BuildStatus below remain
// as wire DTO shapes.

/// <summary>
/// Email notification operations.
/// </summary>
public interface IEmailIntegrationService
{
    /// <summary>Send an email notification</summary>
    Task<IntegrationResult<bool>> SendEmailAsync(string to, string subject, string body);
}

// Epic 31 P3 (4/4): the composite IIntegrationService (zero consumers since
// the Epic 38 cutover) was DELETED. The focused Slack/JIRA/email interfaces
// above stay — they are config-credentialed API-side services, not the git
// platform plane.

// ============================================
// GitHub Integration Models
// ============================================

public class GitHubBranchResult
{
    public bool Success { get; set; }
    public string? BranchName { get; set; }
    public string? BranchUrl { get; set; }

    /// <summary>
    /// The SHA of the base ref the branch was cut from (Story 2.4 AC4 — surfaced
    /// in the <c>BRANCH.CREATED.SUCCESS</c> event/log). Null when not resolved
    /// (e.g. a failed create) or on the legacy default-branch path.
    /// </summary>
    public string? BaseSha { get; set; }

    public string? Error { get; set; }
}

public class GitHubCommit
{
    public string Sha { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public List<string> Files { get; set; } = new();
}

public class CreatePullRequestRequest
{
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Head { get; set; } = string.Empty;
    public string Base { get; set; } = "main";
    public List<string> Reviewers { get; set; } = new();
    public List<string> Labels { get; set; } = new();

    /// <summary>
    /// Open the PR in draft mode (GitHub <c>draft: true</c>). Story 2.8 —
    /// the ADL opens a draft PR up front and flips it to ready after CI /
    /// review pass. Threaded from <c>SingleIssueCycleWorkflow</c>'s
    /// <c>["draft"]=true</c> through the create path to the GitHub REST payload.
    /// </summary>
    public bool IsDraft { get; set; }
}

public class GitHubPullRequestResult
{
    public bool Success { get; set; }
    public int? Number { get; set; }
    public string? Url { get; set; }
    public string? Error { get; set; }
}

public class GitHubMergeResult
{
    public bool Success { get; set; }
    public string? MergeSha { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Epic 38 follow-up #21 — the request to create a GitHub release for a shipped
/// version. Composed engine-side (pure, token-free); the API performs the create
/// with the resolved per-tenant token.
/// </summary>
public class ReleaseCreationRequest
{
    /// <summary>The git tag to create/point the release at (e.g. <c>deploy-a1b2c3d</c>).</summary>
    public string TagName { get; set; } = string.Empty;

    /// <summary>The commit-ish (SHA or branch) the tag is created from. Null/empty ⇒
    /// the repository default branch (GitHub behaviour). Unused if the tag exists.</summary>
    public string? TargetCommitish { get; set; }

    /// <summary>The release title. Empty ⇒ the API falls back to <see cref="TagName"/>.</summary>
    public string? Name { get; set; }

    /// <summary>The release notes / body (Markdown).</summary>
    public string? Body { get; set; }

    /// <summary>Create as a draft (not published) release.</summary>
    public bool Draft { get; set; }

    /// <summary>Mark the release as a pre-release.</summary>
    public bool Prerelease { get; set; }
}

/// <summary>Epic 38 follow-up #21 — the result of a create-release platform call.</summary>
public class GitHubReleaseResult
{
    public bool Success { get; set; }

    /// <summary>The GitHub release id (numeric).</summary>
    public long? Id { get; set; }

    /// <summary>The release's public HTML URL.</summary>
    public string? HtmlUrl { get; set; }

    /// <summary>The tag the release points at.</summary>
    public string? TagName { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// Pull-request lifecycle detail returned by
/// <see cref="IGitHubIntegrationService.GetGitHubPullRequestAsync"/> — backs
/// Story 2-10 idempotency (already-merged → skip re-merge) and the pre-merge
/// readiness/conflict gate.
/// </summary>
public class GitHubPullRequestDetail
{
    public int Number { get; set; }

    /// <summary>open | closed.</summary>
    public string State { get; set; } = "open";

    /// <summary>True when the PR has already been merged.</summary>
    public bool Merged { get; set; }

    /// <summary>The merge commit SHA when <see cref="Merged"/> is true.</summary>
    public string? MergeCommitSha { get; set; }

    /// <summary>
    /// GitHub's <c>mergeable</c> flag: <c>true</c> = no conflicts, <c>false</c> =
    /// conflicts, <c>null</c> = not yet computed (GitHub is still calculating —
    /// the caller must NOT treat unknown as mergeable).
    /// </summary>
    public bool? Mergeable { get; set; }

    /// <summary>
    /// GitHub's <c>mergeable_state</c> (e.g. <c>clean</c>, <c>dirty</c>,
    /// <c>blocked</c>, <c>behind</c>, <c>unstable</c>, <c>unknown</c>). Surfaced
    /// for the readiness gate's blocking-reason message.
    /// </summary>
    public string? MergeableState { get; set; }

    public bool IsDraft { get; set; }

    /// <summary>
    /// The PR's base/target branch (GitHub <c>base.ref</c> — the branch being merged
    /// into). Story 43-12 reads this so the merge gate can resolve the per-target
    /// key (<c>git.merge.dev|qa|main</c>). Empty when the platform response omitted it.
    /// </summary>
    public string BaseBranch { get; set; } = string.Empty;
}

/// <summary>
/// Lightweight reference to an existing pull request — returned by the
/// idempotency lookup (<see cref="IGitHubIntegrationService.GetGitHubOpenPullRequestForBranchAsync"/>).
/// </summary>
public class GitHubPullRequestRef
{
    public int Number { get; set; }
    public string Url { get; set; } = string.Empty;
    public string State { get; set; } = "open";
    public string Title { get; set; } = string.Empty;
    public bool IsDraft { get; set; }
}

public class GitHubFileChange
{
    public string FilePath { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public int Additions { get; set; }
    public int Deletions { get; set; }
}

// ============================================
// CI/CD Integration Models
// ============================================

public class TestRunResult
{
    public string RunId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int TotalTests { get; set; }
    public int PassedTests { get; set; }
    public int FailedTests { get; set; }
    public int SkippedTests { get; set; }
    public double? CoveragePercentage { get; set; }
    public List<TestResult> FailedTestDetails { get; set; } = new();
}

public class TestResult
{
    public string TestName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
    public TimeSpan Duration { get; set; }
}

public class BuildStatus
{
    public string Status { get; set; } = string.Empty;
    public string? BuildUrl { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Error { get; set; }
}

// ============================================
// JIRA Integration Models
// ============================================

public class JiraTicket
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Assignee { get; set; }
    public string? Priority { get; set; }
    public List<string> Labels { get; set; } = new();
}

public class JiraTicketUpdate
{
    public string? Status { get; set; }
    public string? Comment { get; set; }
    public Dictionary<string, object>? CustomFields { get; set; }
}

public class JiraTicketResult
{
    public bool Success { get; set; }
    public string? TicketKey { get; set; }
    public string? Error { get; set; }
}

// ============================================
// GitHub Issue & Review Models (ADL)
// ============================================

public class GitHubIssue
{
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public string State { get; set; } = "open";
    public List<string> Labels { get; set; } = new();
    public string? Assignee { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string Url { get; set; } = string.Empty;
}

public class GitHubReviewComment
{
    public int Id { get; set; }
    public string Body { get; set; } = string.Empty;
    public string? Path { get; set; }
    public int? Line { get; set; }
    public string Author { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
