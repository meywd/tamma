using System.Net.Http.Json;
using System.Text;
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
/// Fetches file contents from the repository. Applies relevance scoring to each file:
///   - Files mentioned in story description: +10
///   - Files in recent commits: +5
///   - Test files: +8
///   - Files in the same directory as changed files: +3
///   - Configuration files: +6
/// </summary>
[Activity(
    "Tamma.Context",
    "Fetch File Contents",
    "Retrieve file contents from the repository with relevance scoring",
    Kind = ActivityKind.Task
)]
public class FetchFileContentsActivity : CodeActivity<FileContentsResult>
{
    private readonly ILogger<FetchFileContentsActivity>? _logger;
    private readonly IIntegrationService? _integrationService;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    /// <summary>Repository URL (e.g., owner/repo)</summary>
    [Input(Description = "Repository URL or identifier")]
    public Input<string> RepositoryUrl { get; set; } = default!;

    /// <summary>Story ID for branch reference</summary>
    [Input(Description = "Story ID")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Specific files to include (optional, overrides automatic detection)</summary>
    [Input(Description = "Specific file paths to include")]
    public Input<List<string>?> TargetFiles { get; set; } = default!;

    /// <summary>Story description text for relevance scoring</summary>
    [Input(Description = "Story description for relevance scoring")]
    public Input<string?> StoryDescription { get; set; } = default!;

    /// <summary>Files from recent commits for relevance scoring</summary>
    [Input(Description = "Files changed in recent commits")]
    public Input<List<string>?> CommitFiles { get; set; } = default!;

    /// <summary>Maximum number of files to fetch</summary>
    [Input(Description = "Maximum files to fetch", DefaultValue = 15)]
    public Input<int> MaxFiles { get; set; } = new(15);

    [JsonConstructor]
    public FetchFileContentsActivity()
    {
    }

    public FetchFileContentsActivity(
        ILogger<FetchFileContentsActivity> logger,
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
        var storyId = StoryId.Get(context);
        var targetFiles = TargetFiles.Get(context);
        var storyDescription = StoryDescription.Get(context);
        var commitFiles = CommitFiles.Get(context);
        var maxFiles = MaxFiles.Get(context);

        _logger?.LogInformation(
            "Fetching file contents for story {StoryId} from {Repo}",
            storyId, repositoryUrl);

        try
        {
            if (string.IsNullOrEmpty(repositoryUrl))
            {
                context.SetResult(new FileContentsResult
                {
                    Success = false,
                    ErrorMessage = "Repository URL is empty"
                });
                return;
            }

            // Determine which files to fetch
            var filePaths = targetFiles?.Any() == true
                ? targetFiles
                : commitFiles?.Distinct().Take(maxFiles).ToList() ?? new List<string>();

            if (!filePaths.Any())
            {
                // Fallback: try to get file changes from the branch
                var fileChanges = await _integrationService!.GetGitHubFileChangesAsync(
                    repositoryUrl, $"feature/{storyId}");
                filePaths = fileChanges.Select(f => f.FilePath).Take(maxFiles).ToList();
            }

            var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? false;

            var files = new List<FileEntry>();
            var totalSize = 0;

            foreach (var filePath in filePaths.Take(maxFiles))
            {
                FileEntry entry;

                if (useMock)
                {
                    entry = CreateMockFileEntry(filePath, storyDescription, commitFiles);
                }
                else
                {
                    entry = await FetchRealFileContentAsync(
                        repositoryUrl, filePath, storyDescription, commitFiles);
                }

                files.Add(entry);
                totalSize += entry.Content?.Length ?? 0;
            }

            // Sort by relevance score descending
            files = files.OrderByDescending(f => f.RelevanceScore).ToList();

            context.SetResult(new FileContentsResult
            {
                Files = files,
                TotalFiles = files.Count,
                TotalSize = totalSize,
                Success = true
            });

            _logger?.LogInformation(
                "Fetched {Count} files ({Size} chars) for story {StoryId}",
                files.Count, totalSize, storyId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Failed to fetch file contents for story {StoryId}", storyId);
            context.SetResult(new FileContentsResult
            {
                Success = false,
                ErrorMessage = $"Failed to fetch files: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Fetch real file content from the GitHub Contents API.
    /// Uses GET /repos/{owner}/{repo}/contents/{path} which returns base64-encoded content.
    /// </summary>
    private async Task<FileEntry> FetchRealFileContentAsync(
        string repositoryUrl,
        string filePath,
        string? storyDescription,
        List<string>? commitFiles)
    {
        if (_httpClientFactory == null)
        {
            _logger?.LogWarning("HttpClientFactory not available, returning mock for {FilePath}", filePath);
            return CreateMockFileEntry(filePath, storyDescription, commitFiles);
        }

        var httpClient = _httpClientFactory.CreateClient("github");

        try
        {
            // GitHub Contents API: GET /repos/{owner}/{repo}/contents/{path}
            var encodedPath = filePath.TrimStart('/');
            var response = await httpClient.GetAsync(
                $"/repos/{repositoryUrl}/contents/{encodedPath}");

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning(
                    "Failed to fetch content for {FilePath} (HTTP {Status})",
                    filePath, response.StatusCode);

                return new FileEntry
                {
                    FilePath = filePath,
                    Content = $"// Failed to retrieve content (HTTP {response.StatusCode})",
                    Language = DetectLanguage(filePath),
                    LineCount = 0,
                    RelevanceScore = CalculateRelevanceScore(filePath, storyDescription, commitFiles)
                };
            }

            var data = await response.Content.ReadFromJsonAsync<JsonElement>();

            string? content = null;
            int lineCount = 0;

            // The Contents API returns base64-encoded content for files
            if (data.TryGetProperty("content", out var contentProp) &&
                contentProp.ValueKind == JsonValueKind.String)
            {
                var base64Content = contentProp.GetString() ?? "";
                // GitHub returns base64 with line breaks, strip them
                base64Content = base64Content.Replace("\n", "").Replace("\r", "");

                try
                {
                    var bytes = Convert.FromBase64String(base64Content);
                    content = Encoding.UTF8.GetString(bytes);
                    lineCount = content.Split('\n').Length;
                }
                catch (FormatException)
                {
                    _logger?.LogWarning("Failed to decode base64 content for {FilePath}", filePath);
                    content = $"// Failed to decode content for {filePath}";
                }
            }
            else if (data.TryGetProperty("message", out var msgProp))
            {
                // Could be a "too large" response — file > 1MB
                content = $"// File too large to retrieve via Contents API: {msgProp.GetString()}";
            }

            return new FileEntry
            {
                FilePath = filePath,
                Content = content ?? $"// No content available for {filePath}",
                Language = DetectLanguage(filePath),
                LineCount = lineCount,
                RelevanceScore = CalculateRelevanceScore(filePath, storyDescription, commitFiles)
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error fetching content for {FilePath}", filePath);
            return new FileEntry
            {
                FilePath = filePath,
                Content = $"// Error retrieving content: {ex.Message}",
                Language = DetectLanguage(filePath),
                LineCount = 0,
                RelevanceScore = CalculateRelevanceScore(filePath, storyDescription, commitFiles)
            };
        }
    }

    /// <summary>
    /// Create a mock file entry with placeholder content.
    /// Used when UseMock=true or as a fallback.
    /// </summary>
    private static FileEntry CreateMockFileEntry(
        string filePath,
        string? storyDescription,
        List<string>? commitFiles)
    {
        return new FileEntry
        {
            FilePath = filePath,
            Content = $"// [MOCK] Content of {filePath}\n// (Mock mode — real file content retrieval disabled)",
            Language = DetectLanguage(filePath),
            LineCount = 2,
            RelevanceScore = CalculateRelevanceScore(filePath, storyDescription, commitFiles)
        };
    }

    /// <summary>
    /// Calculate relevance score for a file based on multiple signals.
    /// </summary>
    private static double CalculateRelevanceScore(
        string filePath,
        string? storyDescription,
        List<string>? commitFiles)
    {
        double score = 0;

        // Files mentioned in story description: +10
        if (!string.IsNullOrEmpty(storyDescription))
        {
            var fileName = Path.GetFileName(filePath);
            var fileNameNoExt = Path.GetFileNameWithoutExtension(filePath);

            if (storyDescription.Contains(fileName, StringComparison.OrdinalIgnoreCase) ||
                storyDescription.Contains(fileNameNoExt, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }
        }

        // Files in recent commits: +5
        if (commitFiles?.Any(cf =>
                cf.Equals(filePath, StringComparison.OrdinalIgnoreCase)) == true)
        {
            score += 5;
        }

        // Test files: +8
        var lowerPath = filePath.ToLowerInvariant();
        if (lowerPath.Contains("test") || lowerPath.Contains("spec") ||
            lowerPath.EndsWith(".test.ts") || lowerPath.EndsWith(".test.cs") ||
            lowerPath.EndsWith(".spec.ts"))
        {
            score += 8;
        }

        // Same directory as changed files: +3
        if (commitFiles != null)
        {
            var fileDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDir) &&
                commitFiles.Any(cf =>
                    Path.GetDirectoryName(cf)?.Equals(fileDir, StringComparison.OrdinalIgnoreCase) == true))
            {
                score += 3;
            }
        }

        // Configuration files: +6
        var configExtensions = new[] { ".json", ".yaml", ".yml", ".toml", ".env", ".config" };
        var configNames = new[] { "appsettings", "package.json", "tsconfig", ".csproj", "dockerfile" };
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var name = Path.GetFileName(filePath).ToLowerInvariant();

        if (configExtensions.Contains(extension) ||
            configNames.Any(cn => name.Contains(cn)))
        {
            score += 6;
        }

        return score;
    }

    private static string DetectLanguage(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
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
}
