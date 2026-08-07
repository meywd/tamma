namespace Tamma.Activities.AI;

// Epic 31 P1 (stage 1, seam 13) — these model classes used to live in
// ContextGatheringActivity.cs. That activity was orphaned (no workflow used
// it) and made direct GitHub REST calls through an unregistered "github"
// named HttpClient, so it was DELETED under the platform-client ratchet.
// The output shape survives because SuggestionGeneratorActivity still takes
// CodeContextOutput as its input.

/// <summary>
/// File change information
/// </summary>
public class FileChange
{
    public string FilePath { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
    public string CommitMessage { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// File content with metadata
/// </summary>
public class FileContent
{
    public string FilePath { get; set; } = string.Empty;
    public string? Content { get; set; }
    public string Language { get; set; } = string.Empty;
    public int LineCount { get; set; }
}

/// <summary>
/// Similar code pattern
/// </summary>
public class SimilarPattern
{
    public string PatternName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? Description { get; set; }
    public double Relevance { get; set; }
}

/// <summary>
/// Test context information
/// </summary>
public class TestContextInfo
{
    public int TotalTests { get; set; }
    public int PassingTests { get; set; }
    public int FailingTests { get; set; }
    public double CoveragePercentage { get; set; }
    public List<FailingTestInfo> FailingTestDetails { get; set; } = new();
}

/// <summary>
/// Failing test details
/// </summary>
public class FailingTestInfo
{
    public string TestName { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
}

/// <summary>
/// Project structure information
/// </summary>
public class ProjectStructure
{
    public string RootDirectory { get; set; } = string.Empty;
    public List<string> MainDirectories { get; set; } = new();
    public List<string> ConfigurationFiles { get; set; } = new();
    public List<string> EntryPoints { get; set; } = new();
}

/// <summary>
/// Session history context
/// </summary>
public class SessionHistoryContext
{
    public int TotalEvents { get; set; }
    public List<StateTransition> StateTransitions { get; set; } = new();
    public List<RecentEvent> RecentEvents { get; set; } = new();
}

/// <summary>
/// State transition record
/// </summary>
public class StateTransition
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Recent event record
/// </summary>
public class RecentEvent
{
    public string EventType { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Output model for context gathering
/// </summary>
public class CodeContextOutput
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string StoryId { get; set; } = string.Empty;
    public string? StoryTitle { get; set; }
    public string? StoryDescription { get; set; }
    public List<string> AcceptanceCriteria { get; set; } = new();
    public Dictionary<string, string> TechnicalRequirements { get; set; } = new();
    public List<FileChange> RecentChanges { get; set; } = new();
    public List<FileContent> FileContents { get; set; } = new();
    public List<SimilarPattern> SimilarPatterns { get; set; } = new();
    public TestContextInfo? TestContext { get; set; }
    public ProjectStructure? ProjectStructure { get; set; }
    public SessionHistoryContext? SessionHistory { get; set; }
    public string? ContextSummary { get; set; }
    public int TotalContextSize { get; set; }
}
