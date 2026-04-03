using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Core.Enums;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.AI;

/// <summary>
/// ELSA activity to generate improvement suggestions based on code context and analysis.
/// Provides actionable recommendations for code quality, best practices, and learning.
/// </summary>
[Activity(
    "Tamma.AI",
    "Suggestion Generator",
    "Generate improvement suggestions based on context and analysis",
    Kind = ActivityKind.Task
)]
public class SuggestionGeneratorActivity : CodeActivity<SuggestionsOutput>
{
    private readonly ILogger<SuggestionGeneratorActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IAnalyticsService? _analyticsService;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>ID of the junior developer</summary>
    [Input(Description = "ID of the junior developer")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Type of suggestions to generate</summary>
    [Input(Description = "Suggestion type: CodeQuality, Architecture, Testing, Performance, Learning")]
    public Input<SuggestionType> SuggestionType { get; set; } = default!;

    /// <summary>Code context from ContextGatheringActivity</summary>
    [Input(Description = "Code context")]
    public Input<CodeContextOutput?> CodeContext { get; set; } = default!;

    /// <summary>Analysis results from ClaudeAnalysisActivity</summary>
    [Input(Description = "Analysis results")]
    public Input<ClaudeAnalysisOutput?> AnalysisResults { get; set; } = default!;

    /// <summary>Maximum number of suggestions to generate</summary>
    [Input(Description = "Maximum suggestions", DefaultValue = 5)]
    public Input<int> MaxSuggestions { get; set; } = new(5);

    [JsonConstructor]
    public SuggestionGeneratorActivity() { }

    public SuggestionGeneratorActivity(
        ILogger<SuggestionGeneratorActivity> logger,
        IMentorshipSessionRepository repository,
        IAnalyticsService analyticsService)
    {
        _logger = logger;
        _repository = repository;
        _analyticsService = analyticsService;
    }

    /// <summary>
    /// Execute the suggestion generation activity
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var juniorId = JuniorId.Get(context);
        var suggestionType = SuggestionType.Get(context);
        var codeContext = CodeContext.Get(context);
        var analysisResults = AnalysisResults.Get(context);
        var maxSuggestions = MaxSuggestions.Get(context);

        _logger?.LogInformation(
            "Generating {Type} suggestions for junior {JuniorId}",
            suggestionType, juniorId);

        try
        {
            // Get junior's profile for personalization
            var junior = await _repository!.GetJuniorByIdAsync(juniorId);

            if (junior == null)
            {
                context.SetResult(new SuggestionsOutput
                {
                    Success = false,
                    Message = "Junior developer not found"
                });
                return;
            }

            // Get behavior patterns for context
            var patterns = await _analyticsService!.DetectPatternsAsync(juniorId);

            // Generate suggestions based on type
            var suggestions = suggestionType switch
            {
                AI.SuggestionType.CodeQuality => GenerateCodeQualitySuggestions(
                    codeContext, analysisResults, junior.SkillLevel, maxSuggestions),

                AI.SuggestionType.Architecture => GenerateArchitectureSuggestions(
                    codeContext, analysisResults, junior.SkillLevel, maxSuggestions),

                AI.SuggestionType.Testing => GenerateTestingSuggestions(
                    codeContext, analysisResults, junior.SkillLevel, maxSuggestions),

                AI.SuggestionType.Performance => GeneratePerformanceSuggestions(
                    codeContext, analysisResults, junior.SkillLevel, maxSuggestions),

                AI.SuggestionType.Learning => GenerateLearningRecommendations(
                    codeContext, analysisResults, junior.SkillLevel, patterns, maxSuggestions),

                _ => new List<Suggestion>()
            };

            // Prioritize suggestions
            suggestions = PrioritizeSuggestions(suggestions, patterns, junior.SkillLevel);

            // Log the suggestion generation
            await _repository!.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.SuggestionsGenerated
            });

            // Record analytics
            await _analyticsService!.RecordMetricAsync(
                sessionId,
                "suggestions_generated",
                suggestions.Count,
                "count");

            _logger?.LogInformation(
                "Generated {Count} suggestions for junior {JuniorId}",
                suggestions.Count, juniorId);

            context.SetResult(new SuggestionsOutput
            {
                Success = true,
                SuggestionType = suggestionType,
                Suggestions = suggestions.Take(maxSuggestions).ToList(),
                TotalGenerated = suggestions.Count,
                Summary = GenerateSummary(suggestions)
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error generating suggestions for session {SessionId}", sessionId);

            context.SetResult(new SuggestionsOutput
            {
                Success = false,
                Message = $"Suggestion generation failed: {ex.Message}"
            });
        }
    }

    private List<Suggestion> GenerateCodeQualitySuggestions(
        CodeContextOutput? context,
        ClaudeAnalysisOutput? analysis,
        int skillLevel,
        int maxCount)
    {
        var suggestions = new List<Suggestion>();

        // Based on code review issues
        if (analysis?.CodeReviewIssues.Any() == true)
        {
            foreach (var issue in analysis.CodeReviewIssues.Take(3))
            {
                suggestions.Add(new Suggestion
                {
                    Category = SuggestionCategory.CodeQuality,
                    Title = $"Fix: {issue.Issue}",
                    Description = issue.Suggestion,
                    Priority = MapSeverityToPriority(issue.Severity),
                    Effort = EstimateEffort(issue.Severity),
                    Impact = MapSeverityToImpact(issue.Severity),
                    Location = issue.Location,
                    ActionItems = new List<string>
                    {
                        $"Review the code at {issue.Location}",
                        "Apply the suggested fix",
                        "Verify the change doesn't break existing tests"
                    }
                });
            }
        }

        // General code quality suggestions based on skill level
        if (skillLevel <= 2)
        {
            suggestions.Add(new Suggestion
            {
                Category = SuggestionCategory.CodeQuality,
                Title = "Use meaningful variable names",
                Description = "Variable names should describe what they contain. For example, use 'userCount' instead of 'x' or 'data'.",
                Priority = Priority.Medium,
                Effort = EffortLevel.Low,
                Impact = ImpactLevel.Medium,
                LearnMoreUrl = "https://wiki.internal/naming-conventions"
            });
        }

        if (context?.FileContents.Any(f => f.LineCount > 100) == true)
        {
            suggestions.Add(new Suggestion
            {
                Category = SuggestionCategory.CodeQuality,
                Title = "Consider breaking up large files",
                Description = "Files with more than 100 lines can be harder to maintain. Consider extracting related code into separate modules.",
                Priority = Priority.Low,
                Effort = EffortLevel.Medium,
                Impact = ImpactLevel.Medium,
                ActionItems = new List<string>
                {
                    "Identify logical groupings of functions",
                    "Extract each group into its own file",
                    "Update imports/references"
                }
            });
        }

        return suggestions;
    }

    private List<Suggestion> GenerateArchitectureSuggestions(
        CodeContextOutput? context,
        ClaudeAnalysisOutput? analysis,
        int skillLevel,
        int maxCount)
    {
        var suggestions = new List<Suggestion>();

        // Based on similar patterns found
        if (context?.SimilarPatterns.Any() == true)
        {
            var topPattern = context.SimilarPatterns.OrderByDescending(p => p.Relevance).First();

            suggestions.Add(new Suggestion
            {
                Category = SuggestionCategory.Architecture,
                Title = $"Follow the {topPattern.PatternName}",
                Description = $"Similar functionality in the codebase uses {topPattern.PatternName}. " +
                             $"See {topPattern.FilePath} for a reference implementation.",
                Priority = Priority.High,
                Effort = EffortLevel.Low,
                Impact = ImpactLevel.High,
                RelatedFiles = new List<string> { topPattern.FilePath },
                ActionItems = new List<string>
                {
                    $"Review the pattern in {topPattern.FilePath}",
                    "Apply the same structure to your implementation",
                    "Ensure consistency with existing code"
                }
            });
        }

        // General architecture guidance
        if (skillLevel <= 3)
        {
            suggestions.Add(new Suggestion
            {
                Category = SuggestionCategory.Architecture,
                Title = "Keep concerns separated",
                Description = "Separate your code into layers: Controllers handle HTTP, Services contain business logic, Repositories handle data access.",
                Priority = Priority.Medium,
                Effort = EffortLevel.Medium,
                Impact = ImpactLevel.High,
                LearnMoreUrl = "https://wiki.internal/architecture-guide"
            });
        }

        // Check for proper dependency injection
        suggestions.Add(new Suggestion
        {
            Category = SuggestionCategory.Architecture,
            Title = "Use dependency injection",
            Description = "Instead of creating dependencies directly, accept them through the constructor. This makes testing easier.",
            Priority = Priority.Medium,
            Effort = EffortLevel.Low,
            Impact = ImpactLevel.High,
            ActionItems = new List<string>
            {
                "Add required dependencies to the constructor",
                "Register dependencies in DI container",
                "Remove 'new' statements for services"
            }
        });

        return suggestions;
    }

    private List<Suggestion> GenerateTestingSuggestions(
        CodeContextOutput? context,
        ClaudeAnalysisOutput? analysis,
        int skillLevel,
        int maxCount)
    {
        var suggestions = new List<Suggestion>();

        // Based on test context
        if (context?.TestContext != null)
        {
            var testContext = context.TestContext;

            if (testContext.CoveragePercentage < 80)
            {
                suggestions.Add(new Suggestion
                {
                    Category = SuggestionCategory.Testing,
                    Title = "Increase test coverage",
                    Description = $"Current coverage is {testContext.CoveragePercentage:F0}%. Target is 80%. Focus on testing the main business logic paths.",
                    Priority = Priority.High,
                    Effort = EffortLevel.Medium,
                    Impact = ImpactLevel.High,
                    ActionItems = new List<string>
                    {
                        "Identify untested code paths",
                        "Write tests for each public method",
                        "Include edge cases and error scenarios"
                    }
                });
            }

            if (testContext.FailingTests > 0)
            {
                foreach (var failingTest in testContext.FailingTestDetails.Take(2))
                {
                    suggestions.Add(new Suggestion
                    {
                        Category = SuggestionCategory.Testing,
                        Title = $"Fix failing test: {failingTest.TestName}",
                        Description = $"Error: {failingTest.ErrorMessage}",
                        Priority = Priority.Critical,
                        Effort = EffortLevel.Medium,
                        Impact = ImpactLevel.Critical,
                        ActionItems = new List<string>
                        {
                            "Read the test name to understand expected behavior",
                            "Compare expected vs actual values in the error",
                            "Debug the code path being tested",
                            "Fix the implementation, not the test"
                        }
                    });
                }
            }
        }

        // General testing advice
        suggestions.Add(new Suggestion
        {
            Category = SuggestionCategory.Testing,
            Title = "Follow the Arrange-Act-Assert pattern",
            Description = "Structure your tests clearly: Arrange (setup), Act (execute), Assert (verify). This makes tests easier to read and maintain.",
            Priority = Priority.Low,
            Effort = EffortLevel.Low,
            Impact = ImpactLevel.Medium,
            LearnMoreUrl = "https://wiki.internal/testing-patterns"
        });

        return suggestions;
    }

    private List<Suggestion> GeneratePerformanceSuggestions(
        CodeContextOutput? context,
        ClaudeAnalysisOutput? analysis,
        int skillLevel,
        int maxCount)
    {
        var suggestions = new List<Suggestion>();

        // General performance suggestions appropriate for skill level
        if (skillLevel >= 3)
        {
            suggestions.Add(new Suggestion
            {
                Category = SuggestionCategory.Performance,
                Title = "Consider async operations for I/O",
                Description = "Database calls, HTTP requests, and file operations should be async to avoid blocking threads.",
                Priority = Priority.Medium,
                Effort = EffortLevel.Medium,
                Impact = ImpactLevel.High,
                ActionItems = new List<string>
                {
                    "Identify synchronous I/O operations",
                    "Convert methods to async/await",
                    "Update callers to await the results"
                }
            });

            suggestions.Add(new Suggestion
            {
                Category = SuggestionCategory.Performance,
                Title = "Avoid N+1 query problems",
                Description = "When loading related data, use eager loading (Include) instead of lazy loading to reduce database round trips.",
                Priority = Priority.Medium,
                Effort = EffortLevel.Medium,
                Impact = ImpactLevel.High,
                LearnMoreUrl = "https://wiki.internal/ef-optimization"
            });
        }

        suggestions.Add(new Suggestion
        {
            Category = SuggestionCategory.Performance,
            Title = "Cache expensive operations",
            Description = "If a computation or query is expensive and results don't change often, consider caching the result.",
            Priority = Priority.Low,
            Effort = EffortLevel.High,
            Impact = ImpactLevel.High,
            ActionItems = new List<string>
            {
                "Identify operations that are slow and repeated",
                "Determine appropriate cache duration",
                "Implement caching with invalidation strategy"
            }
        });

        return suggestions;
    }

    private List<Suggestion> GenerateLearningRecommendations(
        CodeContextOutput? context,
        ClaudeAnalysisOutput? analysis,
        int skillLevel,
        List<BehaviorPattern> patterns,
        int maxCount)
    {
        var suggestions = new List<Suggestion>();

        // Based on identified gaps
        if (analysis?.Gaps.Any() == true)
        {
            foreach (var gap in analysis.Gaps.Take(2))
            {
                suggestions.Add(new Suggestion
                {
                    Category = SuggestionCategory.Learning,
                    Title = $"Learn more about: {gap}",
                    Description = $"Based on your work, this is an area where additional learning would be valuable.",
                    Priority = Priority.Medium,
                    Effort = EffortLevel.Medium,
                    Impact = ImpactLevel.High,
                    IsLearningPath = true,
                    ActionItems = new List<string>
                    {
                        "Search for tutorials on this topic",
                        "Practice with small examples",
                        "Apply the concept in your current work"
                    }
                });
            }
        }

        // Based on behavior patterns
        var concerningPatterns = patterns.Where(p => p.Type == PatternType.Concerning).ToList();
        foreach (var pattern in concerningPatterns.Take(2))
        {
            suggestions.Add(new Suggestion
            {
                Category = SuggestionCategory.Learning,
                Title = pattern.Recommendation ?? "Address behavioral pattern",
                Description = pattern.Description,
                Priority = Priority.Medium,
                Effort = EffortLevel.Low,
                Impact = ImpactLevel.Medium,
                IsLearningPath = true
            });
        }

        // Based on skill level
        var levelRecommendations = GetSkillLevelRecommendations(skillLevel);
        suggestions.AddRange(levelRecommendations);

        // Learning opportunities from code review
        if (analysis?.LearningOpportunities.Any() == true)
        {
            foreach (var opportunity in analysis.LearningOpportunities.Take(2))
            {
                suggestions.Add(new Suggestion
                {
                    Category = SuggestionCategory.Learning,
                    Title = opportunity,
                    Description = $"This is a great opportunity to deepen your understanding of {opportunity}.",
                    Priority = Priority.Low,
                    Effort = EffortLevel.Medium,
                    Impact = ImpactLevel.Medium,
                    IsLearningPath = true
                });
            }
        }

        return suggestions;
    }

    private List<Suggestion> GetSkillLevelRecommendations(int skillLevel)
    {
        var suggestions = new List<Suggestion>();

        switch (skillLevel)
        {
            case 1:
                suggestions.Add(new Suggestion
                {
                    Category = SuggestionCategory.Learning,
                    Title = "Master the basics of the language",
                    Description = "Focus on understanding data types, control flow, and functions. These are the building blocks.",
                    Priority = Priority.High,
                    IsLearningPath = true,
                    LearnMoreUrl = "https://wiki.internal/beginner-guide"
                });
                break;

            case 2:
                suggestions.Add(new Suggestion
                {
                    Category = SuggestionCategory.Learning,
                    Title = "Learn about design patterns",
                    Description = "Understanding common patterns like Factory, Repository, and Observer will help you write better code.",
                    Priority = Priority.Medium,
                    IsLearningPath = true,
                    LearnMoreUrl = "https://wiki.internal/design-patterns"
                });
                break;

            case 3:
                suggestions.Add(new Suggestion
                {
                    Category = SuggestionCategory.Learning,
                    Title = "Explore system design concepts",
                    Description = "Start learning about how larger systems are designed - scalability, reliability, and maintainability.",
                    Priority = Priority.Medium,
                    IsLearningPath = true,
                    LearnMoreUrl = "https://wiki.internal/system-design"
                });
                break;

            case 4:
            case 5:
                suggestions.Add(new Suggestion
                {
                    Category = SuggestionCategory.Learning,
                    Title = "Mentor others",
                    Description = "Teaching is the best way to learn. Consider helping junior developers on the team.",
                    Priority = Priority.Low,
                    IsLearningPath = true
                });
                break;
        }

        return suggestions;
    }

    private List<Suggestion> PrioritizeSuggestions(
        List<Suggestion> suggestions,
        List<BehaviorPattern> patterns,
        int skillLevel)
    {
        // Score each suggestion based on relevance
        foreach (var suggestion in suggestions)
        {
            var score = 0.0;

            // Priority weight
            score += suggestion.Priority switch
            {
                Priority.Critical => 100,
                Priority.High => 75,
                Priority.Medium => 50,
                Priority.Low => 25,
                _ => 0
            };

            // Impact weight
            score += suggestion.Impact switch
            {
                ImpactLevel.Critical => 40,
                ImpactLevel.High => 30,
                ImpactLevel.Medium => 20,
                ImpactLevel.Low => 10,
                _ => 0
            };

            // Effort inverse weight (prefer lower effort)
            score += suggestion.Effort switch
            {
                EffortLevel.Low => 30,
                EffortLevel.Medium => 20,
                EffortLevel.High => 10,
                _ => 0
            };

            // Skill level appropriateness
            if (suggestion.IsLearningPath && skillLevel <= 3)
                score += 20;

            suggestion.RelevanceScore = score;
        }

        return suggestions.OrderByDescending(s => s.RelevanceScore).ToList();
    }

    private Priority MapSeverityToPriority(string severity)
    {
        return severity.ToLower() switch
        {
            "critical" => Priority.Critical,
            "major" => Priority.High,
            "minor" => Priority.Medium,
            "suggestion" => Priority.Low,
            _ => Priority.Medium
        };
    }

    private EffortLevel EstimateEffort(string severity)
    {
        return severity.ToLower() switch
        {
            "critical" => EffortLevel.High,
            "major" => EffortLevel.Medium,
            _ => EffortLevel.Low
        };
    }

    private ImpactLevel MapSeverityToImpact(string severity)
    {
        return severity.ToLower() switch
        {
            "critical" => ImpactLevel.Critical,
            "major" => ImpactLevel.High,
            "minor" => ImpactLevel.Medium,
            _ => ImpactLevel.Low
        };
    }

    private string GenerateSummary(List<Suggestion> suggestions)
    {
        var criticalCount = suggestions.Count(s => s.Priority == Priority.Critical);
        var highCount = suggestions.Count(s => s.Priority == Priority.High);

        if (criticalCount > 0)
            return $"Found {criticalCount} critical issue(s) that need immediate attention.";

        if (highCount > 0)
            return $"Found {highCount} high priority improvement(s) recommended.";

        if (suggestions.Any())
            return $"Generated {suggestions.Count} suggestions for improvement.";

        return "No suggestions at this time - great work!";
    }
}

/// <summary>
/// Types of suggestions that can be generated
/// </summary>
public enum SuggestionType
{
    CodeQuality,
    Architecture,
    Testing,
    Performance,
    Learning
}

/// <summary>
/// Suggestion category
/// </summary>
public enum SuggestionCategory
{
    CodeQuality,
    Architecture,
    Testing,
    Performance,
    Learning,
    BestPractices
}

/// <summary>
/// Suggestion priority
/// </summary>
public enum Priority
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// Effort level
/// </summary>
public enum EffortLevel
{
    Low,
    Medium,
    High
}

/// <summary>
/// Impact level
/// </summary>
public enum ImpactLevel
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// Individual suggestion
/// </summary>
public class Suggestion
{
    public SuggestionCategory Category { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Priority Priority { get; set; }
    public EffortLevel Effort { get; set; }
    public ImpactLevel Impact { get; set; }
    public string? Location { get; set; }
    public List<string> ActionItems { get; set; } = new();
    public List<string> RelatedFiles { get; set; } = new();
    public string? LearnMoreUrl { get; set; }
    public bool IsLearningPath { get; set; }
    public double RelevanceScore { get; set; }
}

/// <summary>
/// Output model for suggestion generator activity
/// </summary>
public class SuggestionsOutput
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public SuggestionType SuggestionType { get; set; }
    public List<Suggestion> Suggestions { get; set; } = new();
    public int TotalGenerated { get; set; }
    public string? Summary { get; set; }
}
