using System.Text.Json.Serialization;

namespace Tamma.Activities.Blocker.Models;

// ============================================
// Enums
// ============================================

/// <summary>
/// The 8 blocker type categories for the Blocker Diagnosis sub-workflow.
/// </summary>
public enum BlockerCategory
{
    /// <summary>Doesn't understand the requirement</summary>
    ConceptualMisunderstanding,

    /// <summary>Lacks specific technical skill (e.g., async/await, SQL)</summary>
    TechnicalKnowledgeGap,

    /// <summary>Tooling, build, or environment problem</summary>
    EnvironmentIssue,

    /// <summary>Can't decide on approach</summary>
    DesignDecisionParalysis,

    /// <summary>Can't find or fix a bug</summary>
    DebuggingStuck,

    /// <summary>Components don't work together</summary>
    IntegrationIssue,

    /// <summary>Blocked by external team, API, or service</summary>
    ExternalDependency,

    /// <summary>Motivation, distraction, or capacity issue</summary>
    PersonalBlocker
}

/// <summary>
/// Blocker severity levels for the sub-workflow.
/// </summary>
public enum BlockerDiagnosisSeverity
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// Progressive resolution levels.
/// </summary>
public enum ResolutionLevel
{
    /// <summary>Socratic method - ask guiding questions</summary>
    Hint,

    /// <summary>Direct guidance with explanations</summary>
    Guidance,

    /// <summary>Code example with detailed explanation</summary>
    Assistance,

    /// <summary>Senior developer intervention</summary>
    Escalation
}

/// <summary>
/// Final status of the blocker resolution.
/// </summary>
public enum BlockerResolutionStatus
{
    Resolved,
    Escalated,
    Timeout
}

// ============================================
// Signal Types (collected in parallel)
// ============================================

/// <summary>
/// Git activity signal: commit frequency, file changes, time since last commit.
/// </summary>
public class GitActivitySignal
{
    public int RecentCommitCount { get; set; }
    public DateTime? LastCommitTime { get; set; }
    public TimeSpan TimeSinceLastCommit { get; set; }
    public int FilesChanged { get; set; }
    public int TotalAdditions { get; set; }
    public int TotalDeletions { get; set; }
    public List<string> ChangedFiles { get; set; } = new();
    public bool CollectionSucceeded { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// CI status signal: build/test results, failure history.
/// </summary>
public class CIStatusSignal
{
    public string BuildStatus { get; set; } = string.Empty;
    public string? BuildError { get; set; }
    public int TotalTests { get; set; }
    public int PassedTests { get; set; }
    public int FailedTests { get; set; }
    public List<string> FailingTestNames { get; set; } = new();
    public double? CoveragePercentage { get; set; }
    public bool CollectionSucceeded { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Inactivity signal: time since last meaningful activity.
/// </summary>
public class InactivitySignal
{
    public TimeSpan TimeSinceLastActivity { get; set; }
    public DateTime? LastActivityTime { get; set; }
    public string? LastActivityType { get; set; }
    public bool IsInactive { get; set; }
    public bool CollectionSucceeded { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Communication signal: messages, questions asked.
/// </summary>
public class CommunicationSignal
{
    public int RecentMessageCount { get; set; }
    public int QuestionsAsked { get; set; }
    public bool HasRecentCommunication { get; set; }
    public string? LastMessageContent { get; set; }
    public DateTime? LastMessageTime { get; set; }
    public bool CollectionSucceeded { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Aggregated signals from all collectors.
/// </summary>
public class AggregatedSignals
{
    public GitActivitySignal? GitActivity { get; set; }
    public CIStatusSignal? CIStatus { get; set; }
    public InactivitySignal? Inactivity { get; set; }
    public CommunicationSignal? Communication { get; set; }
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
    public int SuccessfulCollectors { get; set; }
    public int TotalCollectors { get; set; } = 4;
}

// ============================================
// Diagnosis Output
// ============================================

/// <summary>
/// AI diagnosis result for the blocker.
/// </summary>
public class BlockerDiagnosisResult
{
    public BlockerCategory BlockerType { get; set; }
    public BlockerDiagnosisSeverity Severity { get; set; }
    public string RootCauseHypothesis { get; set; } = string.Empty;
    public string RecommendedApproach { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public List<string> Evidence { get; set; } = new();
}

// ============================================
// Resolution Output
// ============================================

/// <summary>
/// Final output of the Blocker Diagnosis sub-workflow.
/// </summary>
public class BlockerResolution
{
    public BlockerResolutionStatus Status { get; set; }
    public BlockerCategory BlockerType { get; set; }
    public BlockerDiagnosisSeverity BlockerSeverity { get; set; }
    public int Attempts { get; set; }
    public ResolutionLevel ResolutionLevel { get; set; }
    public TimeSpan ResolutionTime { get; set; }
    public string DiagnosisDetails { get; set; } = string.Empty;
    public List<string> FeedbackProvided { get; set; } = new();
}

// ============================================
// Progress Detection
// ============================================

/// <summary>
/// Payload for the progress detection bookmark.
/// </summary>
public class ProgressDetectionPayload
{
    public Guid SessionId { get; set; }
    public string StoryId { get; set; } = string.Empty;
    public string JuniorId { get; set; } = string.Empty;
    public ResolutionLevel CurrentLevel { get; set; }
    public int WaitTimeMinutes { get; set; }
}

/// <summary>
/// Result from progress detection.
/// </summary>
public class ProgressDetectionResult
{
    public bool ProgressDetected { get; set; }
    public string? ProgressType { get; set; }
    public string? Details { get; set; }
}

// ============================================
// Escalation
// ============================================

/// <summary>
/// Context dump for senior escalation.
/// </summary>
public class EscalationContext
{
    public Guid SessionId { get; set; }
    public string StoryId { get; set; } = string.Empty;
    public string JuniorId { get; set; } = string.Empty;
    public BlockerCategory BlockerType { get; set; }
    public BlockerDiagnosisSeverity Severity { get; set; }
    public string DiagnosisDetails { get; set; } = string.Empty;
    public List<string> PreviousAttempts { get; set; } = new();
    public AggregatedSignals? Signals { get; set; }
    public DateTime EscalatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Payload for the senior escalation bookmark.
/// </summary>
public class EscalationPayload
{
    public Guid SessionId { get; set; }
    public string StoryId { get; set; } = string.Empty;
    public string JuniorId { get; set; } = string.Empty;
}

/// <summary>
/// Result from senior response.
/// </summary>
public class EscalationResult
{
    public bool Resolved { get; set; }
    public string? SeniorResponse { get; set; }
    public string? SeniorId { get; set; }
}
