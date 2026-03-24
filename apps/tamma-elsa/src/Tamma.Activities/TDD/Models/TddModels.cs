using System.Text.Json.Serialization;

namespace Tamma.Activities.TDD.Models;

/// <summary>
/// Status of a TDD task execution
/// </summary>
public enum TaskStatus
{
    /// <summary>Task completed successfully through all TDD phases</summary>
    Completed,

    /// <summary>Task failed — GREEN phase could not be resolved</summary>
    Failed,

    /// <summary>Task skipped — not applicable or pre-implemented</summary>
    Skipped
}

/// <summary>
/// Final result of a TDD cycle for a single task
/// </summary>
public class TaskResult
{
    public TaskStatus Status { get; set; }
    public int TestsWritten { get; set; }
    public int TestsPassing { get; set; }
    public List<string> FilesChanged { get; set; } = new();
    public string CommitSha { get; set; } = string.Empty;
    public PhaseResult RedPhaseResult { get; set; } = new();
    public PhaseResult GreenPhaseResult { get; set; } = new();
    public PhaseResult? RefactorPhaseResult { get; set; }
    public bool DebuggingInvoked { get; set; }
}

/// <summary>
/// Result of an individual TDD phase (RED, GREEN, or REFACTOR)
/// </summary>
public class PhaseResult
{
    /// <summary>Phase name: RED, GREEN, or REFACTOR</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>Whether the phase completed successfully</summary>
    public bool Succeeded { get; set; }

    /// <summary>Duration of the phase</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Number of iterations (rewrites in RED, debug attempts in GREEN)</summary>
    public int Iterations { get; set; }

    /// <summary>Additional notes about the phase outcome</summary>
    public string? Notes { get; set; }
}

/// <summary>
/// Result from the test generation LLM call
/// </summary>
public class TestGenerationResult
{
    public bool Success { get; set; }
    public string TestCode { get; set; } = string.Empty;
    public List<string> TestFiles { get; set; } = new();
    public int TestCount { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result from the implementation generation LLM call
/// </summary>
public class ImplementationResult
{
    public bool Success { get; set; }
    public string ImplementationCode { get; set; } = string.Empty;
    public List<string> ImplementationFiles { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result from the code analysis / refactoring review LLM call
/// </summary>
public class RefactoringAnalysis
{
    public bool HasSuggestions { get; set; }
    public double Confidence { get; set; }
    public List<RefactoringSuggestion> Suggestions { get; set; } = new();
}

/// <summary>
/// A single refactoring suggestion from the code reviewer
/// </summary>
public class RefactoringSuggestion
{
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string? FilePath { get; set; }
}

/// <summary>
/// Result from applying refactoring
/// </summary>
public class RefactoringResult
{
    public bool Success { get; set; }
    public string RefactoredCode { get; set; } = string.Empty;
    public List<string> FilesChanged { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result from a test run (subset or full suite)
/// </summary>
public class TestRunResult
{
    public bool AllPassed { get; set; }
    public int TotalTests { get; set; }
    public int PassedTests { get; set; }
    public int FailedTests { get; set; }
    public List<string> FailureMessages { get; set; } = new();
    public string? RawOutput { get; set; }
}

/// <summary>
/// Result from a git commit operation
/// </summary>
public class CommitResult
{
    public bool Success { get; set; }
    public string CommitSha { get; set; } = string.Empty;
    public string CommitMessage { get; set; } = string.Empty;
    public List<string> FilesCommitted { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Task object from the implementation plan, passed as workflow input
/// </summary>
public class TddTask
{
    public string Description { get; set; } = string.Empty;
    public List<string> Files { get; set; } = new();
    public string? Scope { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Prompt detail level derived from skill level
/// </summary>
public static class SkillLevelPromptDetail
{
    public static string GetDetail(int skillLevel) => skillLevel switch
    {
        1 => "very_detailed",
        2 => "detailed",
        3 => "standard",
        4 => "concise",
        5 => "minimal",
        _ => "standard"
    };

    public static string GetTestPromptGuidance(int skillLevel) => skillLevel switch
    {
        1 or 2 => "Provide very detailed test structure with comments explaining each test case, " +
                   "assert patterns, and why each test is important. Include setup/teardown examples.",
        3 => "Write standard test cases covering happy path, edge cases, and error conditions.",
        4 or 5 => "Provide high-level test specifications. The developer will fill in the details. " +
                   "Focus on what to test, not how to test it.",
        _ => "Write standard test cases covering happy path, edge cases, and error conditions."
    };

    public static string GetImplementationGuidance(int skillLevel) => skillLevel switch
    {
        1 or 2 => "Provide step-by-step implementation with detailed comments explaining each decision. " +
                   "Include explanations of design patterns used.",
        3 => "Write a clean implementation following project conventions. Include brief comments for complex logic.",
        4 or 5 => "Write minimal implementation to pass tests. Focus on clean design patterns and SOLID principles.",
        _ => "Write a clean implementation following project conventions."
    };

    public static string GetRefactoringGuidance(int skillLevel) => skillLevel switch
    {
        1 or 2 => "Suggest simple, safe refactorings like renaming, extracting methods, and reducing duplication.",
        3 => "Suggest standard refactorings including design pattern application and code organization.",
        4 or 5 => "Focus on advanced design patterns, performance optimizations, and architectural improvements.",
        _ => "Suggest standard refactorings including design pattern application and code organization."
    };
}
