using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
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
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

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
        IIntegrationService integrationService,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _integrationService = integrationService;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
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

            var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? false;

            List<PatternMatch> patterns;
            if (useMock)
            {
                _logger?.LogInformation("Using mock pattern discovery for '{StoryTitle}'", storyTitle);
                patterns = SimulatePatternDiscovery(storyTitle, storyTags, maxPatterns);
            }
            else
            {
                patterns = await DiscoverPatternsFromRepoAsync(
                    repositoryUrl, storyTitle, storyTags, maxPatterns);
            }

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
    /// Discover code patterns by scanning the repository tree via GitHub API.
    /// Matches file names and paths against story keywords to find relevant patterns.
    /// </summary>
    private async Task<List<PatternMatch>> DiscoverPatternsFromRepoAsync(
        string repositoryUrl,
        string storyTitle,
        string[]? tags,
        int maxPatterns)
    {
        if (_httpClientFactory == null)
        {
            _logger?.LogWarning("HttpClientFactory not available, falling back to mock patterns");
            return SimulatePatternDiscovery(storyTitle, tags, maxPatterns);
        }

        var httpClient = _httpClientFactory.CreateClient("github");

        try
        {
            // Fetch the repository tree recursively
            var treeResponse = await httpClient.GetAsync(
                $"/repos/{repositoryUrl}/git/trees/main?recursive=1");

            if (!treeResponse.IsSuccessStatusCode)
            {
                // Try master branch
                treeResponse = await httpClient.GetAsync(
                    $"/repos/{repositoryUrl}/git/trees/master?recursive=1");
            }

            if (!treeResponse.IsSuccessStatusCode)
            {
                _logger?.LogWarning(
                    "Failed to fetch repository tree for {Repo} (HTTP {Status}), falling back to mock",
                    repositoryUrl, treeResponse.StatusCode);
                return SimulatePatternDiscovery(storyTitle, tags, maxPatterns);
            }

            var treeData = await treeResponse.Content.ReadFromJsonAsync<JsonElement>();
            var treeEntries = treeData.GetProperty("tree");

            // Extract keywords from story title and tags
            var keywords = ExtractKeywords(storyTitle, tags);

            var patterns = new List<PatternMatch>();

            foreach (var entry in treeEntries.EnumerateArray())
            {
                var path = entry.GetProperty("path").GetString() ?? "";
                var type = entry.GetProperty("type").GetString() ?? "";

                // Only consider source files (blobs), not directories
                if (type != "blob")
                    continue;

                // Skip non-code files
                if (!IsCodeFile(path))
                    continue;

                var relevance = CalculateFileRelevance(path, keywords);

                if (relevance > 0.3)
                {
                    var patternName = InferPatternName(path);
                    var description = InferPatternDescription(path);

                    patterns.Add(new PatternMatch
                    {
                        PatternName = patternName,
                        FilePath = path,
                        Description = description,
                        Relevance = relevance
                    });
                }
            }

            if (patterns.Count == 0)
            {
                _logger?.LogInformation(
                    "No matching patterns found in repo tree for '{StoryTitle}', returning empty",
                    storyTitle);
            }

            return patterns
                .OrderByDescending(p => p.Relevance)
                .Take(maxPatterns)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Error scanning repository tree for patterns, falling back to mock");
            return SimulatePatternDiscovery(storyTitle, tags, maxPatterns);
        }
    }

    /// <summary>
    /// Extract meaningful keywords from the story title and tags for file matching.
    /// </summary>
    private static List<string> ExtractKeywords(string storyTitle, string[]? tags)
    {
        var keywords = new List<string>();

        // Split title into words and keep meaningful ones (3+ chars, not common stop words)
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "with", "from", "into", "that", "this", "have", "will",
            "should", "could", "would", "can", "not", "are", "was", "were", "been", "being",
            "has", "had", "does", "did", "but", "its", "all", "any", "each", "new", "add",
            "use", "set", "get", "implement", "create", "update", "delete", "remove"
        };

        var titleWords = storyTitle
            .Split(new[] { ' ', '-', '_', '.', ',', ':', ';', '/', '(', ')', '[', ']' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !stopWords.Contains(w))
            .Select(w => w.ToLowerInvariant());

        keywords.AddRange(titleWords);

        if (tags != null)
        {
            keywords.AddRange(tags.Select(t => t.ToLowerInvariant()));
        }

        return keywords.Distinct().ToList();
    }

    /// <summary>
    /// Calculate relevance of a file path to the story keywords (0.0 to 1.0).
    /// </summary>
    private static double CalculateFileRelevance(string filePath, List<string> keywords)
    {
        if (keywords.Count == 0)
            return 0;

        var pathLower = filePath.ToLowerInvariant();
        var fileName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();

        int matches = 0;
        double totalScore = 0;

        foreach (var keyword in keywords)
        {
            if (fileName.Contains(keyword))
            {
                // File name match is strongest signal
                totalScore += 1.0;
                matches++;
            }
            else if (pathLower.Contains(keyword))
            {
                // Path match is weaker
                totalScore += 0.5;
                matches++;
            }
        }

        if (matches == 0)
            return 0;

        // Normalize: ratio of matched keywords, weighted by match quality
        return Math.Min(1.0, totalScore / keywords.Count);
    }

    /// <summary>
    /// Infer a human-readable pattern name from a file path.
    /// </summary>
    private static string InferPatternName(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var dirName = Path.GetDirectoryName(filePath)?.Replace('\\', '/');
        var lastDir = dirName?.Split('/').LastOrDefault(d => !string.IsNullOrEmpty(d)) ?? "";

        // Common suffixes that indicate patterns
        var patternSuffixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Controller", "Controller Pattern" },
            { "Service", "Service Pattern" },
            { "Repository", "Repository Pattern" },
            { "Handler", "Handler Pattern" },
            { "Factory", "Factory Pattern" },
            { "Provider", "Provider Pattern" },
            { "Middleware", "Middleware Pattern" },
            { "Validator", "Validator Pattern" },
            { "Activity", "Activity Pattern" },
            { "Workflow", "Workflow Pattern" },
            { "Command", "Command Pattern" },
            { "Query", "Query Pattern" },
            { "Event", "Event Pattern" },
            { "Model", "Model Pattern" },
            { "Interface", "Interface Pattern" },
        };

        foreach (var (suffix, patternName) in patternSuffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return $"{fileName} ({patternName})";
            }
        }

        // Fall back to directory-based naming
        if (!string.IsNullOrEmpty(lastDir))
        {
            return $"{fileName} (in {lastDir})";
        }

        return fileName;
    }

    /// <summary>
    /// Generate a description for a pattern based on its file path.
    /// </summary>
    private static string InferPatternDescription(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var language = extension switch
        {
            ".cs" => "C#",
            ".ts" => "TypeScript",
            ".tsx" => "TypeScript/React",
            ".js" => "JavaScript",
            ".py" => "Python",
            ".java" => "Java",
            ".go" => "Go",
            ".rs" => "Rust",
            _ => "source"
        };

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        return $"Existing {language} implementation: {fileName} at {filePath}";
    }

    /// <summary>
    /// Check if a file path represents a code file worth examining.
    /// </summary>
    private static bool IsCodeFile(string path)
    {
        var codeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".go", ".rs",
            ".rb", ".php", ".kt", ".swift", ".scala", ".vue", ".svelte"
        };

        var extension = Path.GetExtension(path);
        if (!codeExtensions.Contains(extension))
            return false;

        // Skip common non-pattern files
        var pathLower = path.ToLowerInvariant();
        if (pathLower.Contains("node_modules/") ||
            pathLower.Contains("bin/") ||
            pathLower.Contains("obj/") ||
            pathLower.Contains(".min.") ||
            pathLower.Contains("dist/") ||
            pathLower.Contains("migrations/"))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Mock fallback: simulated pattern discovery based on common code structures.
    /// Used when UseMock=true or when GitHub API is unavailable.
    /// </summary>
    private static List<PatternMatch> SimulatePatternDiscovery(
        string storyTitle,
        string[]? tags,
        int maxPatterns)
    {
        var patterns = new List<PatternMatch>();
        var titleLower = storyTitle.ToLowerInvariant();

        if (titleLower.Contains("api") || titleLower.Contains("controller") ||
            titleLower.Contains("endpoint"))
        {
            patterns.Add(new PatternMatch
            {
                PatternName = "REST Controller Pattern",
                FilePath = "src/Controllers/ExampleController.cs",
                Description = "[MOCK] Standard REST API controller with CRUD operations and validation",
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
                Description = "[MOCK] Service class with dependency injection and async operations",
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
                Description = "[MOCK] Data access using repository pattern with EF Core",
                Relevance = 0.75
            });
        }

        if (patterns.Count == 0)
        {
            patterns.Add(new PatternMatch
            {
                PatternName = "Standard Implementation Pattern",
                FilePath = "src/Services/ExampleService.cs",
                Description = "[MOCK] Reference implementation following project conventions",
                Relevance = 0.60
            });
        }

        if (tags?.Any() == true)
        {
            foreach (var tag in tags.Take(2))
            {
                patterns.Add(new PatternMatch
                {
                    PatternName = $"{tag} Pattern",
                    FilePath = $"src/{tag}/Example{tag}.cs",
                    Description = $"[MOCK] Implementation pattern for {tag} functionality",
                    Relevance = 0.65
                });
            }
        }

        return patterns.OrderByDescending(p => p.Relevance).Take(maxPatterns).ToList();
    }
}
