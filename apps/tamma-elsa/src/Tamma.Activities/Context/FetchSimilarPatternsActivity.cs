using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Context.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Context;

/// <summary>
/// Searches the repository for code patterns similar to what the story requires.
/// Returns matching patterns with relevance scores for the developer to reference.
/// This is typically the lowest-priority context source and will be trimmed first.
/// </summary>
[Activity(
    "Tamma.Context",
    "Fetch Similar Patterns",
    "Search repository for similar code patterns and implementations",
    Kind = ActivityKind.Task
)]
public class FetchSimilarPatternsActivity : CodeActivity<SimilarPatternsResult>
{
    private readonly ILogger<FetchSimilarPatternsActivity>? _logger;
    private readonly IIntegrationService? _integrationService;

    /// <summary>Repository URL (e.g., owner/repo)</summary>
    [Input(Description = "Repository URL or identifier")]
    public Input<string> RepositoryUrl { get; set; } = default!;

    /// <summary>Story title to search patterns for</summary>
    [Input(Description = "Story title for pattern matching")]
    public Input<string> StoryTitle { get; set; } = default!;

    /// <summary>Story tags for additional pattern matching</summary>
    [Input(Description = "Story tags for additional matching")]
    public Input<string[]?> StoryTags { get; set; } = default!;

    /// <summary>Maximum number of patterns to return</summary>
    [Input(Description = "Maximum patterns to return", DefaultValue = 5)]
    public Input<int> MaxPatterns { get; set; } = new(5);

    [JsonConstructor]
    public FetchSimilarPatternsActivity()
    {
    }

    public FetchSimilarPatternsActivity(
        ILogger<FetchSimilarPatternsActivity> logger,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _integrationService = integrationService;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repositoryUrl = RepositoryUrl.Get(context);
        var storyTitle = StoryTitle.Get(context);
        var storyTags = StoryTags.Get(context);
        var maxPatterns = MaxPatterns.Get(context);

        _logger?.LogInformation(
            "Searching for similar patterns for '{StoryTitle}' in {Repo}",
            storyTitle, repositoryUrl);

        try
        {
            if (string.IsNullOrEmpty(repositoryUrl))
            {
                context.SetResult(new SimilarPatternsResult
                {
                    Success = false,
                    ErrorMessage = "Repository URL is empty"
                });
                return;
            }

            // In production, this would use code search APIs, embeddings, or AST analysis.
            // For now, we simulate pattern discovery based on common code structures.
            var patterns = await DiscoverPatternsAsync(
                repositoryUrl, storyTitle, storyTags, maxPatterns);

            context.SetResult(new SimilarPatternsResult
            {
                Patterns = patterns,
                Success = true
            });

            _logger?.LogInformation(
                "Found {Count} similar patterns for '{StoryTitle}'",
                patterns.Count, storyTitle);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Failed to fetch similar patterns for '{StoryTitle}'", storyTitle);
            context.SetResult(new SimilarPatternsResult
            {
                Success = false,
                ErrorMessage = $"Failed to fetch similar patterns: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Discover code patterns similar to the story requirements.
    /// In production, this would leverage code search, embeddings, or static analysis.
    /// </summary>
    private Task<List<PatternMatch>> DiscoverPatternsAsync(
        string repositoryUrl,
        string storyTitle,
        string[]? tags,
        int maxPatterns)
    {
        // Simulated pattern discovery.
        // A real implementation would call a code search service or analyze the AST.
        var patterns = new List<PatternMatch>();

        var titleLower = storyTitle.ToLowerInvariant();

        if (titleLower.Contains("api") || titleLower.Contains("controller") ||
            titleLower.Contains("endpoint"))
        {
            patterns.Add(new PatternMatch
            {
                PatternName = "REST Controller Pattern",
                FilePath = "src/Controllers/ExampleController.cs",
                Description = "Standard REST API controller with CRUD operations and validation",
                Relevance = 0.85
            });
        }

        if (titleLower.Contains("service") || titleLower.Contains("business") ||
            titleLower.Contains("logic"))
        {
            patterns.Add(new PatternMatch
            {
                PatternName = "Service Layer Pattern",
                FilePath = "src/Services/ExampleService.cs",
                Description = "Service class with dependency injection and async operations",
                Relevance = 0.80
            });
        }

        if (titleLower.Contains("data") || titleLower.Contains("repository") ||
            titleLower.Contains("database"))
        {
            patterns.Add(new PatternMatch
            {
                PatternName = "Repository Pattern",
                FilePath = "src/Repositories/ExampleRepository.cs",
                Description = "Data access using repository pattern with EF Core",
                Relevance = 0.75
            });
        }

        // Always include a generic patterns entry
        if (patterns.Count == 0)
        {
            patterns.Add(new PatternMatch
            {
                PatternName = "Standard Implementation Pattern",
                FilePath = "src/Services/ExampleService.cs",
                Description = "Reference implementation following project conventions",
                Relevance = 0.60
            });
        }

        // Add tag-based patterns
        if (tags?.Any() == true)
        {
            foreach (var tag in tags.Take(2))
            {
                patterns.Add(new PatternMatch
                {
                    PatternName = $"{tag} Pattern",
                    FilePath = $"src/{tag}/Example{tag}.cs",
                    Description = $"Implementation pattern for {tag} functionality",
                    Relevance = 0.65
                });
            }
        }

        return Task.FromResult(
            patterns.OrderByDescending(p => p.Relevance).Take(maxPatterns).ToList());
    }
}
