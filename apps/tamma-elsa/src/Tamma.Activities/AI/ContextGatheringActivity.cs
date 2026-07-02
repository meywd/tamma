using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.ADL;
using Tamma.Activities.LlmCall;
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
    private readonly TammaApiClient? _apiClient;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

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

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public ContextGatheringActivity() { }

    /// <summary>
    /// Story 38 (Phase 2) — thin-client DI constructor. No <c>IIntegrationService</c>
    /// and no git token: recent commits + the CI test summary are read through the
    /// git/CI mediation endpoints via <see cref="TammaApiClient"/>. The GitHub
    /// Contents/tree reads keep using the injected <see cref="IHttpClientFactory"/>.
    /// </summary>
    public ContextGatheringActivity(
        ILogger<ContextGatheringActivity> logger,
        IMentorshipSessionRepository repository,
        TammaApiClient apiClient,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _repository = repository;
        _apiClient = apiClient;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
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
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.Get(context));
        var correlationId = context.WorkflowExecutionContext.Id;
        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var ct = context.CancellationToken;

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
                var recentChanges = await GatherRecentChanges(apiClient, story.RepositoryUrl, storyId, correlationId, tenantId, ct);
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
                    var testContext = await GatherTestContext(apiClient, story.RepositoryUrl, storyId, correlationId, tenantId, ct);
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

    private bool UseMock => _configuration?.GetValue<bool>("Anthropic:UseMock") ?? false;

    private async Task<List<FileChange>> GatherRecentChanges(
        TammaApiClient apiClient, string repositoryUrl, string storyId,
        string correlationId, string? tenantId, CancellationToken ct)
    {
        try
        {
            var commitsResponse = await apiClient.GetCommitsAsync(
                repositoryUrl,
                $"feature/{storyId}",
                DateTime.UtcNow.AddDays(-7),
                correlationId, tenantId, ct);
            if (commitsResponse is null || !commitsResponse.Success)
                throw new InvalidOperationException(
                    commitsResponse?.FailureReason ?? "git mediation endpoint unavailable");
            var commits = GitMediationMapping.ToCommits(commitsResponse.Commits);

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

    /// <summary>
    /// Gather file contents from the repository.
    /// In real mode, fetches actual content via the GitHub Contents API.
    /// In mock mode, returns placeholder content.
    /// </summary>
    private async Task<List<FileContent>> GatherFileContents(
        string repositoryUrl,
        string storyId,
        List<string> filePaths)
    {
        var contents = new List<FileContent>();

        if (UseMock || _httpClientFactory == null)
        {
            _logger?.LogInformation("Using mock file contents for {Count} files", filePaths.Count);
            foreach (var filePath in filePaths.Take(10))
            {
                contents.Add(new FileContent
                {
                    FilePath = filePath,
                    Content = $"// [MOCK] Content of {filePath}\n// (Mock mode — real file content retrieval disabled)",
                    Language = DetectLanguage(filePath),
                    LineCount = 2
                });
            }
            return contents;
        }

        var httpClient = _httpClientFactory.CreateClient("github");

        foreach (var filePath in filePaths.Take(10))
        {
            try
            {
                var encodedPath = filePath.TrimStart('/');
                var response = await httpClient.GetAsync(
                    $"/repos/{repositoryUrl}/contents/{encodedPath}");

                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogWarning(
                        "Failed to fetch content for {FilePath} (HTTP {Status})",
                        filePath, response.StatusCode);

                    contents.Add(new FileContent
                    {
                        FilePath = filePath,
                        Content = $"// Failed to retrieve content (HTTP {response.StatusCode})",
                        Language = DetectLanguage(filePath),
                        LineCount = 0
                    });
                    continue;
                }

                var data = await response.Content.ReadFromJsonAsync<JsonElement>();

                string? content = null;
                int lineCount = 0;

                if (data.TryGetProperty("content", out var contentProp) &&
                    contentProp.ValueKind == JsonValueKind.String)
                {
                    var base64Content = contentProp.GetString() ?? "";
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
                    content = $"// File too large for Contents API: {msgProp.GetString()}";
                }

                contents.Add(new FileContent
                {
                    FilePath = filePath,
                    Content = content ?? $"// No content available for {filePath}",
                    Language = DetectLanguage(filePath),
                    LineCount = lineCount
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error fetching content for {FilePath}", filePath);
                contents.Add(new FileContent
                {
                    FilePath = filePath,
                    Content = $"// Error retrieving content: {ex.Message}",
                    Language = DetectLanguage(filePath),
                    LineCount = 0
                });
            }
        }

        return contents;
    }

    /// <summary>
    /// Gather similar patterns by scanning the repository tree.
    /// In real mode, fetches the repo tree via GitHub API and matches files against story keywords.
    /// In mock mode, returns hardcoded example patterns.
    /// </summary>
    private async Task<List<SimilarPattern>> GatherSimilarPatterns(string repositoryUrl, string storyTitle)
    {
        if (UseMock || _httpClientFactory == null)
        {
            _logger?.LogInformation("Using mock similar patterns for '{StoryTitle}'", storyTitle);
            return new List<SimilarPattern>
            {
                new SimilarPattern
                {
                    PatternName = "Controller Pattern",
                    FilePath = "src/Controllers/ExampleController.cs",
                    Description = "[MOCK] Example of a REST API controller with standard CRUD operations",
                    Relevance = 0.85
                },
                new SimilarPattern
                {
                    PatternName = "Service Layer",
                    FilePath = "src/Services/ExampleService.cs",
                    Description = "[MOCK] Example of a service class with dependency injection",
                    Relevance = 0.78
                },
                new SimilarPattern
                {
                    PatternName = "Repository Pattern",
                    FilePath = "src/Repositories/ExampleRepository.cs",
                    Description = "[MOCK] Example of data access using repository pattern",
                    Relevance = 0.72
                }
            };
        }

        var httpClient = _httpClientFactory.CreateClient("github");

        try
        {
            // Fetch the repository tree recursively
            var treeResponse = await httpClient.GetAsync(
                $"/repos/{repositoryUrl}/git/trees/main?recursive=1");

            if (!treeResponse.IsSuccessStatusCode)
            {
                treeResponse = await httpClient.GetAsync(
                    $"/repos/{repositoryUrl}/git/trees/master?recursive=1");
            }

            if (!treeResponse.IsSuccessStatusCode)
            {
                _logger?.LogWarning(
                    "Failed to fetch repository tree for {Repo}, returning empty patterns",
                    repositoryUrl);
                return new List<SimilarPattern>();
            }

            var treeData = await treeResponse.Content.ReadFromJsonAsync<JsonElement>();
            var treeEntries = treeData.GetProperty("tree");

            // Extract keywords from story title
            var keywords = ExtractKeywords(storyTitle);
            var patterns = new List<SimilarPattern>();

            foreach (var entry in treeEntries.EnumerateArray())
            {
                var path = entry.GetProperty("path").GetString() ?? "";
                var type = entry.GetProperty("type").GetString() ?? "";

                if (type != "blob" || !IsCodeFile(path))
                    continue;

                var relevance = CalculateFileRelevance(path, keywords);

                if (relevance > 0.3)
                {
                    var fileName = Path.GetFileNameWithoutExtension(path);
                    var extension = Path.GetExtension(path).ToLowerInvariant();
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

                    patterns.Add(new SimilarPattern
                    {
                        PatternName = InferPatternName(path),
                        FilePath = path,
                        Description = $"Existing {language} implementation: {fileName} at {path}",
                        Relevance = relevance
                    });
                }
            }

            return patterns.OrderByDescending(p => p.Relevance).Take(5).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error scanning repository for similar patterns");
            return new List<SimilarPattern>();
        }
    }

    private async Task<TestContextInfo> GatherTestContext(
        TammaApiClient apiClient, string repositoryUrl, string storyId,
        string correlationId, string? tenantId, CancellationToken ct)
    {
        try
        {
            var ciResponse = await apiClient.TriggerTestsAsync(
                repositoryUrl,
                new LlmCall.Models.CiTriggerTestsRequest
                {
                    Branch = $"feature/{storyId}",
                    CorrelationId = correlationId,
                },
                tenantId, ct);
            if (ciResponse is null || !ciResponse.Success)
                throw new InvalidOperationException(
                    ciResponse?.FailureReason ?? "ci mediation endpoint unavailable");
            var testResults = GitMediationMapping.ToTestRun(ciResponse.TestRun);

            return new TestContextInfo
            {
                TotalTests = testResults.TotalTests,
                PassingTests = testResults.PassedTests,
                FailingTests = testResults.FailedTests,
                CoveragePercentage = testResults.CoveragePercentage ?? 0,
                // Per-test failure detail is not returned by the CI-mediation endpoint —
                // FailedTestDetails is empty (aggregate counts only).
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

    /// <summary>
    /// Gather project structure by scanning the repository tree via GitHub API.
    /// In real mode, calls GET /repos/{owner}/{repo}/git/trees/{branch}?recursive=1
    /// and parses the result into a structured project overview.
    /// In mock mode, returns a hardcoded structure.
    /// </summary>
    private async Task<ProjectStructure> GatherProjectStructure(string repositoryUrl)
    {
        if (UseMock || _httpClientFactory == null)
        {
            _logger?.LogInformation("Using mock project structure for {Repo}", repositoryUrl);
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

        var httpClient = _httpClientFactory.CreateClient("github");

        try
        {
            // Fetch the repository tree recursively
            var treeResponse = await httpClient.GetAsync(
                $"/repos/{repositoryUrl}/git/trees/main?recursive=1");

            if (!treeResponse.IsSuccessStatusCode)
            {
                treeResponse = await httpClient.GetAsync(
                    $"/repos/{repositoryUrl}/git/trees/master?recursive=1");
            }

            if (!treeResponse.IsSuccessStatusCode)
            {
                _logger?.LogWarning(
                    "Failed to fetch repository tree for project structure (HTTP {Status})",
                    treeResponse.StatusCode);
                return new ProjectStructure { RootDirectory = "/" };
            }

            var treeData = await treeResponse.Content.ReadFromJsonAsync<JsonElement>();
            var treeEntries = treeData.GetProperty("tree");

            var directories = new HashSet<string>();
            var configFiles = new List<string>();
            var entryPoints = new List<string>();

            var configExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".json", ".yaml", ".yml", ".toml", ".env", ".config", ".xml", ".csproj", ".sln"
            };
            var configNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "appsettings.json", "appsettings.development.json", "appsettings.production.json",
                "package.json", "tsconfig.json", "docker-compose.yml", "dockerfile",
                ".env", ".env.example", "pnpm-workspace.yaml", "turbo.json"
            };
            var entryPointNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "program.cs", "startup.cs", "main.ts", "main.js", "index.ts", "index.js",
                "app.ts", "app.js", "server.ts", "server.js", "main.go", "main.py",
                "serve.ts", "serve.js"
            };

            foreach (var entry in treeEntries.EnumerateArray())
            {
                var path = entry.GetProperty("path").GetString() ?? "";
                var type = entry.GetProperty("type").GetString() ?? "";

                if (type == "tree")
                {
                    // Only collect top-level and second-level directories
                    var depth = path.Count(c => c == '/');
                    if (depth <= 1)
                    {
                        directories.Add(path);
                    }
                }
                else if (type == "blob")
                {
                    var fileName = Path.GetFileName(path).ToLowerInvariant();
                    var extension = Path.GetExtension(path).ToLowerInvariant();

                    // Check for config files
                    if (configNames.Contains(fileName) || configExtensions.Contains(extension))
                    {
                        // Only include config files in root or first-level directories
                        var depth = path.Count(c => c == '/');
                        if (depth <= 1)
                        {
                            configFiles.Add(path);
                        }
                    }

                    // Check for entry points
                    if (entryPointNames.Contains(fileName))
                    {
                        entryPoints.Add(path);
                    }
                }
            }

            return new ProjectStructure
            {
                RootDirectory = "/",
                MainDirectories = directories.OrderBy(d => d).ToList(),
                ConfigurationFiles = configFiles.OrderBy(f => f).ToList(),
                EntryPoints = entryPoints.OrderBy(f => f).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error gathering project structure for {Repo}", repositoryUrl);
            return new ProjectStructure { RootDirectory = "/" };
        }
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

    // ================================================================
    // Helper methods shared with GatherSimilarPatterns
    // ================================================================

    private static List<string> ExtractKeywords(string text)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "with", "from", "into", "that", "this", "have", "will",
            "should", "could", "would", "can", "not", "are", "was", "were", "been", "being",
            "has", "had", "does", "did", "but", "its", "all", "any", "each", "new", "add",
            "use", "set", "get", "implement", "create", "update", "delete", "remove"
        };

        return text
            .Split(new[] { ' ', '-', '_', '.', ',', ':', ';', '/', '(', ')', '[', ']' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !stopWords.Contains(w))
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    private static double CalculateFileRelevance(string filePath, List<string> keywords)
    {
        if (keywords.Count == 0)
            return 0;

        var pathLower = filePath.ToLowerInvariant();
        var fileName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();

        double totalScore = 0;
        int matches = 0;

        foreach (var keyword in keywords)
        {
            if (fileName.Contains(keyword))
            {
                totalScore += 1.0;
                matches++;
            }
            else if (pathLower.Contains(keyword))
            {
                totalScore += 0.5;
                matches++;
            }
        }

        if (matches == 0)
            return 0;

        return Math.Min(1.0, totalScore / keywords.Count);
    }

    private static string InferPatternName(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var lastDir = Path.GetDirectoryName(filePath)?.Replace('\\', '/')
            ?.Split('/').LastOrDefault(d => !string.IsNullOrEmpty(d)) ?? "";

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
        };

        foreach (var (suffix, patternName) in patternSuffixes)
        {
            if (fileName.Contains(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return $"{fileName} ({patternName})";
            }
        }

        return !string.IsNullOrEmpty(lastDir) ? $"{fileName} (in {lastDir})" : fileName;
    }

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
