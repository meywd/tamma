using System.Text.Json.Serialization;

namespace Tamma.Activities.Context.Models;

// ============================================
// Enums
// ============================================

/// <summary>
/// The purpose of context gathering, which determines priority ordering for budget trimming.
/// Higher-priority purposes retain more context when trimming is needed.
/// </summary>
public enum ContextPurpose
{
    /// <summary>Diagnosing a blocker or issue (highest context need)</summary>
    Diagnosis = 0,

    /// <summary>Reviewing code or implementation</summary>
    Review = 1,

    /// <summary>Assessing junior developer understanding</summary>
    Assessment = 2,

    /// <summary>Planning implementation approach</summary>
    Planning = 3,

    /// <summary>Active implementation assistance (lowest context need)</summary>
    Implementation = 4
}

/// <summary>
/// Priority levels for context sources, used during budget trimming.
/// Lower numeric value = higher priority (trimmed last).
/// </summary>
public enum ContextSourcePriority
{
    /// <summary>Critical context that should be preserved (e.g., story metadata)</summary>
    Critical = 0,

    /// <summary>High priority context (e.g., test results for diagnosis)</summary>
    High = 1,

    /// <summary>Medium priority context (e.g., file contents)</summary>
    Medium = 2,

    /// <summary>Low priority context (e.g., similar patterns)</summary>
    Low = 3,

    /// <summary>Optional context, trimmed first</summary>
    Optional = 4
}

// ============================================
// DTOs for individual fetch activities
// ============================================

/// <summary>
/// Story metadata gathered from the repository.
/// </summary>
public class StoryMetadata
{
    public string StoryId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> AcceptanceCriteria { get; set; } = new();
    public Dictionary<string, string> TechnicalRequirements { get; set; } = new();
    public string? RepositoryUrl { get; set; }
    public int Priority { get; set; }
    public int Complexity { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// A single commit entry from recent commits fetch.
/// </summary>
public class CommitEntry
{
    public string Sha { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public List<string> Files { get; set; } = new();
    public int Additions { get; set; }
    public int Deletions { get; set; }
}

/// <summary>
/// Result of fetching recent commits.
/// </summary>
public class RecentCommitsResult
{
    public List<CommitEntry> Commits { get; set; } = new();
    public int TotalCommits { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// A single file with its contents and metadata.
/// </summary>
public class FileEntry
{
    public string FilePath { get; set; } = string.Empty;
    public string? Content { get; set; }
    public string Language { get; set; } = string.Empty;
    public int LineCount { get; set; }

    /// <summary>Relevance score (0-100) for budget trimming prioritization.</summary>
    public double RelevanceScore { get; set; }
}

/// <summary>
/// Result of fetching file contents.
/// </summary>
public class FileContentsResult
{
    public List<FileEntry> Files { get; set; } = new();
    public int TotalFiles { get; set; }
    public int TotalSize { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result of fetching test results.
/// </summary>
public class TestResultsData
{
    public int TotalTests { get; set; }
    public int PassingTests { get; set; }
    public int FailingTests { get; set; }
    public int SkippedTests { get; set; }
    public double CoveragePercentage { get; set; }
    public List<FailingTestDetail> FailingTestDetails { get; set; } = new();
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Details about a failing test.
/// </summary>
public class FailingTestDetail
{
    public string TestName { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
}

/// <summary>
/// A single event from session history.
/// </summary>
public class SessionEvent
{
    public string EventType { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string? StateFrom { get; set; }
    public string? StateTo { get; set; }
}

/// <summary>
/// Result of fetching session history.
/// </summary>
public class SessionHistoryResult
{
    public int TotalEvents { get; set; }
    public List<SessionEvent> Events { get; set; } = new();
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// A similar code pattern found in the repository.
/// </summary>
public class PatternMatch
{
    public string PatternName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? Description { get; set; }
    public double Relevance { get; set; }
}

/// <summary>
/// Result of fetching similar patterns.
/// </summary>
public class SimilarPatternsResult
{
    public List<PatternMatch> Patterns { get; set; } = new();
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

// ============================================
// Context source with priority for budget trimming
// ============================================

/// <summary>
/// A context source section with its priority and estimated character size,
/// used for priority-based budget trimming.
/// </summary>
public class ContextSection
{
    public string Name { get; set; } = string.Empty;
    public ContextSourcePriority Priority { get; set; }
    public int EstimatedSize { get; set; }
    public object? Data { get; set; }
    public bool Trimmed { get; set; }
}

// ============================================
// Assembled context (before and after budget)
// ============================================

/// <summary>
/// The fully assembled context before budget trimming.
/// This is the input to the ApplyBudgetActivity.
/// </summary>
public class AssembledContext
{
    public StoryMetadata? StoryMetadata { get; set; }
    public RecentCommitsResult? RecentCommits { get; set; }
    public FileContentsResult? FileContents { get; set; }
    public TestResultsData? TestResults { get; set; }
    public SessionHistoryResult? SessionHistory { get; set; }
    public SimilarPatternsResult? SimilarPatterns { get; set; }
    public ContextPurpose Purpose { get; set; }
    public int TotalEstimatedSize { get; set; }
    public List<ContextSection> Sections { get; set; } = new();
}

// ============================================
// Final output from the sub-workflow
// ============================================

/// <summary>
/// The final output from the Context Gathering sub-workflow,
/// after budget trimming has been applied.
/// </summary>
public class ContextGatheringOutput
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string StoryId { get; set; } = string.Empty;
    public ContextPurpose Purpose { get; set; }

    // Gathered data sections
    public StoryMetadata? StoryMetadata { get; set; }
    public RecentCommitsResult? RecentCommits { get; set; }
    public FileContentsResult? FileContents { get; set; }
    public TestResultsData? TestResults { get; set; }
    public SessionHistoryResult? SessionHistory { get; set; }
    public SimilarPatternsResult? SimilarPatterns { get; set; }

    // Budget info
    public int TotalContextSize { get; set; }
    public int BudgetLimit { get; set; }
    public int SectionsTrimmed { get; set; }
    public string? ContextSummary { get; set; }
}
