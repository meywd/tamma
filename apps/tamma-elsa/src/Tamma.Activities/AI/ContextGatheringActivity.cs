using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.AI;

/// <summary>
/// ELSA activity to gather relevant code context for AI analysis.
/// Collects repository information, recent changes, related files, and documentation.
/// </summary>
[Activity(
    "Tamma.AI",
    "Context Gathering",
    "Gather relevant code context for AI analysis",
    Kind = ActivityKind.Task
)]
public class ContextGatheringActivity : CodeActivity<CodeContextOutput>
{
    private readonly ILogger<ContextGatheringActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IIntegrationService? _integrationService;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>ID of the story for context</summary>
    [Input(Description = "ID of the story")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Specific files to include in context (optional)</summary>
    [Input(Description = "Specific files to include")]
    public Input<List<string>?> TargetFiles { get; set; } = default!;

    /// <summary>Maximum context size in characters</summary>
    [Input(Description = "Maximum context size", DefaultValue = 50000)]
    public Input<int> MaxContextSize { get; set; } = new(50000);

    /// <summary>Include similar code patterns</summary>
    [Input(Description = "Include similar patterns", DefaultValue = true)]
    public Input<bool> IncludeSimilarPatterns { get; set; } = new(true);

    /// <summary>Include test files</summary>
    [Input(Description = "Include test files", DefaultValue = true)]
    public Input<bool> IncludeTests { get; set; } = new(true);

    [JsonConstructor]
    public ContextGatheringActivity() { }

    public ContextGatheringActivity(
        ILogger<ContextGatheringActivity> logger,
        IMentorshipSessionRepository repository,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _repository = repository;
        _integrationService = integrationService;
    }

    /// <summary>
    /// Execute the context gathering activity
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var targetFiles = TargetFiles.Get(context);
        var maxContextSize = MaxContextSize.Get(context);
        var includeSimilarPatterns = IncludeSimilarPatterns.Get(context);
        var includeTests = IncludeTests.Get(context);

        _logger?.LogInformation(
            "Gathering context for story {StoryId} in session {SessionId}",
            storyId, sessionId);

        try
        {
            var story = await _repository!.GetStoryByIdAsync(storyId);

            if (story == null)
            {
                context.SetResult(new CodeContextOutput
                {
                    Success = false,
                    Message = $"Story {storyId} not found"
                });
                return;
            }

            var codeContext = new CodeContextOutput
            {
                Success = true,
                StoryId = storyId,
                StoryTitle = story.Title,
                StoryDescription = story.Description
            };

            // Gather different types of context
            if (!string.IsNullOrEmpty(story.RepositoryUrl))
            {
                // 1. Get recent changes
                var recentChanges = await GatherRecentChanges(story.RepositoryUrl, storyId);
                codeContext.RecentChanges = recentChanges;

                // 2. Get target file contents
                if (targetFiles?.Any() == true)
                {
                    var fileContents = await GatherFileContents(story.RepositoryUrl, storyId, targetFiles);
                    codeContext.FileContents = fileContents;
                }
                else
                {
                    // Get files from recent changes
                    var changedFiles = recentChanges.Select(c => c.FilePath).Distinct().Take(10).ToList();
                    var fileContents = await GatherFileContents(story.RepositoryUrl, storyId, changedFiles);
                    codeContext.FileContents = fileContents;
                }

                // 3. Get similar patterns if requested
                if (includeSimilarPatterns)
                {
                    var patterns = await GatherSimilarPatterns(story.RepositoryUrl, story.Title);
                    codeContext.SimilarPatterns = patterns;
                }

                // 4. Get test files if requested
                if (includeTests)
                {
                    var testContext = await GatherTestContext(story.RepositoryUrl, storyId);
                    codeContext.TestContext = testContext;
                }

                // 5. Get project structure
                var structure = await GatherProjectStructure(story.RepositoryUrl);
                codeContext.ProjectStructure = structure;
            }

            // 6. Gather session history context
            var sessionHistory = await GatherSessionHistory(sessionId);
            codeContext.SessionHistory = sessionHistory;

            // 7. Get acceptance criteria
            codeContext.AcceptanceCriteria = ParseAcceptanceCriteria(story.AcceptanceCriteria?.RootElement.GetRawText());

            // 8. Get technical requirements
            codeContext.TechnicalRequirements = ParseTechnicalRequirements(story.TechnicalRequirements?.RootElement.GetRawText());

            // Trim context if too large
            TrimContextToSize(codeContext, maxContextSize);

            // Calculate context summary
            codeContext.ContextSummary = GenerateContextSummary(codeContext);
            codeContext.TotalContextSize = CalculateContextSize(codeContext);

            _logger?.LogInformation(
                "Context gathered for story {StoryId}: {FileCount} files, {Size} chars",
                storyId, codeContext.FileContents.Count, codeContext.TotalContextSize);

            context.SetResult(codeContext);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error gathering context for session {SessionId}", sessionId);

            context.SetResult(new CodeContextOutput
            {
                Success = false,
                Message = $"Context gathering failed: {ex.Message}"
            });
        }
    }

    private async Task<List<FileChange>> GatherRecentChanges(string repositoryUrl, string storyId)
    {
        try
        {
            var commits = await _integrationService!.GetGitHubCommitsAsync(
                repositoryUrl,
                $"feature/{storyId}",
                DateTime.UtcNow.AddDays(-7));

            var changes = new List<FileChange>();

            foreach (var commit in commits.Take(10))
            {
                foreach (var file in commit.Files)
                {
                    changes.Add(new FileChange
                    {
                        FilePath = file,
                        CommitSha = commit.Sha,
                        CommitMessage = commit.Message,
                        Author = commit.Author,
                        Timestamp = commit.Timestamp
                    });
                }
            }

            return changes.GroupBy(c => c.FilePath)
                .Select(g => g.OrderByDescending(c => c.Timestamp).First())
                .ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to gather recent changes");
            return new List<FileChange>();
        }
    }

    private async Task<List<FileContent>> GatherFileContents(
        string repositoryUrl,
        string storyId,
        List<string> filePaths)
    {
        var contents = new List<FileContent>();

        // Simulate file content retrieval
        // In production, this would call GitHub API to get file contents
        foreach (var filePath in filePaths.Take(10))
        {
            contents.Add(new FileContent
            {
                FilePath = filePath,
                Content = $"// Content of {filePath}\n// (In production, actual file content would be here)",
                Language = DetectLanguage(filePath),
                LineCount = 50 // Simulated
            });
        }

        return contents;
    }

    private async Task<List<SimilarPattern>> GatherSimilarPatterns(string repositoryUrl, string storyTitle)
    {
        // Simulate finding similar patterns
        // In production, this would search the codebase for similar implementations
        var patterns = new List<SimilarPattern>
        {
            new SimilarPattern
            {
                PatternName = "Controller Pattern",
                FilePath = "src/Controllers/ExampleController.cs",
                Description = "Example of a REST API controller with standard CRUD operations",
                Relevance = 0.85
            },
            new SimilarPattern
            {
                PatternName = "Service Layer",
                FilePath = "src/Services/ExampleService.cs",
                Description = "Example of a service class with dependency injection",
                Relevance = 0.78
            },
            new SimilarPattern
            {
                PatternName = "Repository Pattern",
                FilePath = "src/Repositories/ExampleRepository.cs",
                Description = "Example of data access using repository pattern",
                Relevance = 0.72
            }
        };

        return patterns;
    }

    private async Task<TestContextInfo> GatherTestContext(string repositoryUrl, string storyId)
    {
        try
        {
            var testResults = await _integrationService!.TriggerTestsAsync(
                repositoryUrl,
                $"feature/{storyId}");

            return new TestContextInfo
            {
                TotalTests = testResults.TotalTests,
                PassingTests = testResults.PassedTests,
                FailingTests = testResults.FailedTests,
                CoveragePercentage = testResults.CoveragePercentage ?? 0,
                FailingTestDetails = testResults.FailedTestDetails.Select(t => new FailingTestInfo
                {
                    TestName = t.TestName,
                    ErrorMessage = t.ErrorMessage ?? "No error message",
                    StackTrace = t.StackTrace
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to gather test context");
            return new TestContextInfo();
        }
    }

    private async Task<ProjectStructure> GatherProjectStructure(string repositoryUrl)
    {
        // Simulate project structure
        // In production, this would analyze the repository structure
        return new ProjectStructure
        {
            RootDirectory = "/",
            MainDirectories = new List<string>
            {
                "src/Controllers",
                "src/Services",
                "src/Repositories",
                "src/Models",
                "tests"
            },
            ConfigurationFiles = new List<string>
            {
                "appsettings.json",
                "package.json",
                ".csproj"
            },
            EntryPoints = new List<string>
            {
                "Program.cs",
                "Startup.cs"
            }
        };
    }

    private async Task<SessionHistoryContext> GatherSessionHistory(Guid sessionId)
    {
        var events = await _repository!.GetEventsBySessionIdAsync(sessionId);

        return new SessionHistoryContext
        {
            TotalEvents = events.Count,
            StateTransitions = events
                .Where(e => e.StateFrom.HasValue && e.StateTo.HasValue)
                .Select(e => new StateTransition
                {
                    From = e.StateFrom!.Value.ToString(),
                    To = e.StateTo!.Value.ToString(),
                    Timestamp = e.CreatedAt
                })
                .ToList(),
            RecentEvents = events
                .OrderByDescending(e => e.CreatedAt)
                .Take(10)
                .Select(e => new RecentEvent
                {
                    EventType = e.EventType.ToString(),
                    Timestamp = e.CreatedAt
                })
                .ToList()
        };
    }

    private List<string> ParseAcceptanceCriteria(string? criteria)
    {
        if (string.IsNullOrEmpty(criteria))
            return new List<string>();

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(criteria) ?? new List<string>();
        }
        catch
        {
            return new List<string> { criteria };
        }
    }

    private Dictionary<string, string> ParseTechnicalRequirements(string? requirements)
    {
        if (string.IsNullOrEmpty(requirements))
            return new Dictionary<string, string>();

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(requirements)
                ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string> { { "raw", requirements } };
        }
    }

    private string DetectLanguage(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLower();
        return extension switch
        {
            ".cs" => "csharp",
            ".ts" => "typescript",
            ".tsx" => "typescript",
            ".js" => "javascript",
            ".jsx" => "javascript",
            ".py" => "python",
            ".java" => "java",
            ".go" => "go",
            ".rs" => "rust",
            ".sql" => "sql",
            ".json" => "json",
            ".yaml" or ".yml" => "yaml",
            ".md" => "markdown",
            _ => "plaintext"
        };
    }

    private void TrimContextToSize(CodeContextOutput context, int maxSize)
    {
        var currentSize = CalculateContextSize(context);

        if (currentSize <= maxSize)
            return;

        // Trim file contents first (largest usually)
        while (context.FileContents.Count > 1 && CalculateContextSize(context) > maxSize)
        {
            context.FileContents.RemoveAt(context.FileContents.Count - 1);
        }

        // Trim similar patterns
        while (context.SimilarPatterns.Count > 1 && CalculateContextSize(context) > maxSize)
        {
            context.SimilarPatterns.RemoveAt(context.SimilarPatterns.Count - 1);
        }

        // Trim session history
        if (context.SessionHistory != null && CalculateContextSize(context) > maxSize)
        {
            context.SessionHistory.RecentEvents = context.SessionHistory.RecentEvents.Take(5).ToList();
            context.SessionHistory.StateTransitions = context.SessionHistory.StateTransitions.Take(5).ToList();
        }
    }

    private int CalculateContextSize(CodeContextOutput context)
    {
        var size = 0;

        size += context.StoryDescription?.Length ?? 0;
        size += context.FileContents.Sum(f => f.Content?.Length ?? 0);
        size += context.SimilarPatterns.Sum(p => p.Description?.Length ?? 0);
        size += context.AcceptanceCriteria.Sum(c => c.Length);

        if (context.TestContext != null)
        {
            size += context.TestContext.FailingTestDetails.Sum(t =>
                (t.ErrorMessage?.Length ?? 0) + (t.StackTrace?.Length ?? 0));
        }

        return size;
    }

    private string GenerateContextSummary(CodeContextOutput context)
    {
        var parts = new List<string>();

        parts.Add($"Story: {context.StoryTitle}");

        if (context.FileContents.Any())
            parts.Add($"Files: {context.FileContents.Count} ({string.Join(", ", context.FileContents.Select(f => Path.GetFileName(f.FilePath)))})");

        if (context.RecentChanges.Any())
            parts.Add($"Recent changes: {context.RecentChanges.Count}");

        if (context.TestContext != null)
            parts.Add($"Tests: {context.TestContext.PassingTests}/{context.TestContext.TotalTests} passing");

        if (context.AcceptanceCriteria.Any())
            parts.Add($"Acceptance criteria: {context.AcceptanceCriteria.Count}");

        return string.Join(" | ", parts);
    }
}

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
/// Output model for context gathering activity
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
