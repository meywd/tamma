using System.Text.Json.Serialization;

namespace Tamma.Activities.Review.Models;

// ============================================
// Enums
// ============================================

/// <summary>
/// Status of a pull request review
/// </summary>
public enum PRReviewStatus
{
    /// <summary>Review is pending — no reviewer has submitted yet</summary>
    Pending,

    /// <summary>Reviewer approved the PR</summary>
    Approved,

    /// <summary>Reviewer requested changes</summary>
    ChangesRequested,

    /// <summary>Review was dismissed</summary>
    Dismissed,

    /// <summary>Review timed out waiting for a response</summary>
    TimedOut,

    /// <summary>An error occurred during review monitoring</summary>
    Error
}

/// <summary>
/// Merge strategy for pull requests
/// </summary>
public enum MergeStrategy
{
    /// <summary>Squash all commits into one (default)</summary>
    Squash,

    /// <summary>Create a merge commit</summary>
    Merge,

    /// <summary>Rebase commits onto base branch</summary>
    Rebase
}

/// <summary>
/// Severity level for review comments
/// </summary>
public enum ReviewCommentSeverity
{
    /// <summary>Critical issue that must be fixed</summary>
    Critical,

    /// <summary>Major issue that should be fixed</summary>
    Major,

    /// <summary>Minor issue — nice to fix</summary>
    Minor,

    /// <summary>Suggestion for improvement</summary>
    Suggestion,

    /// <summary>Informational / praise</summary>
    Info
}

/// <summary>
/// Escalation reason
/// </summary>
public enum EscalationReason
{
    /// <summary>Maximum fix iterations reached</summary>
    MaxIterationsReached,

    /// <summary>Review timed out</summary>
    ReviewTimeout,

    /// <summary>Critical issue found that junior cannot resolve</summary>
    CriticalIssue,

    /// <summary>Merge conflict that requires senior intervention</summary>
    MergeConflict,

    /// <summary>Other reason</summary>
    Other
}

// ============================================
// DTOs
// ============================================

/// <summary>
/// Result of creating a pull request
/// </summary>
public class PRCreationResult
{
    public bool Success { get; set; }
    public int? PRNumber { get; set; }
    public string? PRUrl { get; set; }
    public string? HeadBranch { get; set; }
    public string? BaseBranch { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Result from a review submission (webhook payload)
/// </summary>
public class ReviewResult
{
    public PRReviewStatus Status { get; set; }
    public string? ReviewerLogin { get; set; }
    public string? ReviewBody { get; set; }
    public List<ReviewCommentDetail> Comments { get; set; } = new();
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Detailed review comment
/// </summary>
public class ReviewCommentDetail
{
    public string FilePath { get; set; } = string.Empty;
    public int? LineNumber { get; set; }
    public string Body { get; set; } = string.Empty;
    public ReviewCommentSeverity Severity { get; set; } = ReviewCommentSeverity.Minor;
    public string? SuggestedFix { get; set; }
    public string Author { get; set; } = string.Empty;
}

/// <summary>
/// Guidance delivered to the junior for fixing review comments
/// </summary>
public class FixGuidance
{
    public int Iteration { get; set; }
    public List<CommentFixGuidance> Items { get; set; } = new();
    public string? OverallMessage { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Guidance for a single review comment fix
/// </summary>
public class CommentFixGuidance
{
    public string OriginalComment { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int? LineNumber { get; set; }
    public string Guidance { get; set; } = string.Empty;
    public string? CodeExample { get; set; }
    public ReviewCommentSeverity Severity { get; set; }
}

/// <summary>
/// Input payload when fixes are submitted (resumes WaitForFixesActivity)
/// </summary>
public class FixesSubmittedPayload
{
    public string SessionId { get; set; } = string.Empty;
    public int PRNumber { get; set; }
    public int Iteration { get; set; }
    public string? CommitSha { get; set; }
    public List<string> FilesChanged { get; set; } = new();
    public string? Message { get; set; }
}

/// <summary>
/// Input payload when a review webhook fires (resumes MonitorReviewActivity)
/// </summary>
public class ReviewWebhookPayload
{
    public string SessionId { get; set; } = string.Empty;
    public int PRNumber { get; set; }
    public PRReviewStatus Status { get; set; }
    public string? ReviewerLogin { get; set; }
    public string? ReviewBody { get; set; }
    public List<ReviewCommentDetail> Comments { get; set; } = new();
}

/// <summary>
/// Input payload when a senior responds to an escalation (resumes EscalateReviewActivity)
/// </summary>
public class EscalationResponsePayload
{
    public string SessionId { get; set; } = string.Empty;
    public int PRNumber { get; set; }
    public string ResponderId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // "approve", "fix", "reject"
    public string? Message { get; set; }
    public string? FixCommitSha { get; set; }
}

/// <summary>
/// Merge result from completing the review
/// </summary>
public class ReviewMergeResult
{
    public bool Success { get; set; }
    public string? MergeSha { get; set; }
    public MergeStrategy StrategyUsed { get; set; }
    public string? Error { get; set; }
    public DateTime MergedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Escalation record
/// </summary>
public class EscalationRecord
{
    public EscalationReason Reason { get; set; }
    public string? Message { get; set; }
    public int FixIterationsAttempted { get; set; }
    public List<string> UnresolvedComments { get; set; } = new();
    public DateTime EscalatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Summary of the code review sub-workflow execution
/// </summary>
public class CodeReviewWorkflowResult
{
    public bool Success { get; set; }
    public PRReviewStatus FinalStatus { get; set; }
    public int? PRNumber { get; set; }
    public string? PRUrl { get; set; }
    public string? MergeSha { get; set; }
    public int TotalIterations { get; set; }
    public int ReviewRounds { get; set; }
    public bool WasEscalated { get; set; }
    public string? EscalationResolution { get; set; }
    public string? Message { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
