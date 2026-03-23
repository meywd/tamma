namespace Tamma.Activities.ADL.Models;

// ============================================
// Enums
// ============================================

/// <summary>
/// Exit reason for a single issue cycle
/// </summary>
public enum CycleExitReason
{
    Success,
    NoIssues,
    PlanRejected,
    TddFailed,
    CiFailed,
    MergeRejected,
    MergeFailed,
    Error
}

/// <summary>
/// Approval decision for plan or merge checkpoints
/// </summary>
public enum ApprovalDecision
{
    Approve,
    Reject,
    Edit,
    Test
}

// ============================================
// DTOs
// ============================================

/// <summary>
/// Represents a GitHub issue selected for autonomous development
/// </summary>
public class AdlIssue
{
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public List<string> Labels { get; set; } = new();
    public string Url { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// AI-generated implementation plan for an issue
/// </summary>
public class AdlPlan
{
    public string IssueTitle { get; set; } = string.Empty;
    public int IssueNumber { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<string> Steps { get; set; } = new();
    public List<string> FilesToModify { get; set; } = new();
    public List<string> FilesToCreate { get; set; } = new();
    public string? TestStrategy { get; set; }
    public string? EstimatedComplexity { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Result of a human approval checkpoint (plan or merge)
/// </summary>
public class ApprovalResult
{
    public ApprovalDecision Decision { get; set; }
    public string? Feedback { get; set; }
    public string? EditedPlan { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime DecidedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Result of a single issue cycle
/// </summary>
public class CycleResult
{
    public CycleExitReason ExitReason { get; set; }
    public int? IssueNumber { get; set; }
    public string? IssueTitle { get; set; }
    public string? BranchName { get; set; }
    public int? PrNumber { get; set; }
    public string? PrUrl { get; set; }
    public string? MergeSha { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Configuration for the ADL orchestrator
/// </summary>
public class AdlConfig
{
    public string Repository { get; set; } = string.Empty;
    public string[] IssueLabels { get; set; } = new[] { "tamma-auto" };
    public string BotAssignee { get; set; } = "tamma-bot";
    public string BaseBranch { get; set; } = "main";
    public int MaxIssuesPerRun { get; set; } = 10;
    public int CooldownSeconds { get; set; } = 10;
    public OperationalLimits Limits { get; set; } = new();
}

/// <summary>
/// Operational limits for the ADL orchestrator
/// </summary>
public class OperationalLimits
{
    public int DailyIssueQuota { get; set; } = 20;
    public decimal DailyBudgetUsd { get; set; } = 50.0m;
    public bool EmergencyStop { get; set; }
    public TimeSpan MaxCycleDuration { get; set; } = TimeSpan.FromHours(2);
}

/// <summary>
/// Review analysis result from AI
/// </summary>
public class ReviewAnalysisResult
{
    public bool HasActionableComments { get; set; }
    public int TotalComments { get; set; }
    public int ActionableComments { get; set; }
    public List<ReviewFixItem> FixItems { get; set; } = new();
    public string? Summary { get; set; }
}

/// <summary>
/// A single review comment that needs to be addressed
/// </summary>
public class ReviewFixItem
{
    public string FilePath { get; set; } = string.Empty;
    public int? Line { get; set; }
    public string Comment { get; set; } = string.Empty;
    public string? SuggestedFix { get; set; }
    public string Priority { get; set; } = "normal";
}
