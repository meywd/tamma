using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.TDD.Models;

namespace Tamma.Activities.TDD;

/// <summary>
/// ELSA activity that analyzes recently written code for refactoring opportunities.
/// Part of the REFACTOR phase in the TDD cycle.
/// Calls LLM with role=reviewer to identify improvements.
/// Returns suggestions with confidence scores; the workflow decides whether to apply them.
/// </summary>
[Activity(
    "Tamma.TDD",
    "Analyze Code",
    "Identify refactoring opportunities in recently written code (REFACTOR phase)",
    Kind = ActivityKind.Task
)]
public class AnalyzeCodeActivity : CodeActivity<RefactoringAnalysis>
{
    private readonly ILogger<AnalyzeCodeActivity>? _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story identifier</summary>
    [Input(Description = "Story identifier")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>The test code written in RED phase</summary>
    [Input(Description = "Test code written in RED phase")]
    public Input<string> TestCode { get; set; } = default!;

    /// <summary>The implementation code written in GREEN phase</summary>
    [Input(Description = "Implementation code written in GREEN phase")]
    public Input<string> ImplementationCode { get; set; } = default!;

    /// <summary>Junior developer's skill level (1-5)</summary>
    [Input(Description = "Junior skill level (1-5)", DefaultValue = 3)]
    public Input<int> SkillLevel { get; set; } = new(3);

    /// <summary>Confidence threshold for suggesting refactoring</summary>
    [Input(Description = "Confidence threshold for refactoring suggestions", DefaultValue = 0.6)]
    public Input<double> ConfidenceThreshold { get; set; } = new(0.6);

    [JsonConstructor]
    public AnalyzeCodeActivity() { }

    public AnalyzeCodeActivity(
        ILogger<AnalyzeCodeActivity> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var testCode = TestCode.Get(context);
        var implementationCode = ImplementationCode.Get(context);
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);
        var confidenceThreshold = ConfidenceThreshold.Get(context);

        _logger?.LogInformation(
            "TDD REFACTOR phase: Analyzing code for story {StoryId}, session {SessionId}",
            storyId, sessionId);

        try
        {
            var prompt = BuildAnalysisPrompt(testCode, implementationCode, skillLevel);

            var callbackUrl = _configuration?["Engine:CallbackUrl"];
            var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? false;

            string response;
            if (useMock)
            {
                response = SimulateAnalysis();
            }
            else if (!string.IsNullOrEmpty(callbackUrl))
            {
                response = await CallEngineCallback(callbackUrl, prompt);
            }
            else
            {
                response = await CallLlm(prompt);
            }

            var result = ParseAnalysisResponse(response, confidenceThreshold);

            _logger?.LogInformation(
                "TDD REFACTOR phase: Found {SuggestionCount} suggestions (hasSuggestions={HasSuggestions}, confidence={Confidence:F2}) for session {SessionId}",
                result.Suggestions.Count, result.HasSuggestions, result.Confidence, sessionId);

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TDD REFACTOR phase: Error analyzing code for session {SessionId}", sessionId);

            // On error, skip refactoring (safe default)
            context.SetResult(new RefactoringAnalysis
            {
                HasSuggestions = false,
                Confidence = 0,
                Suggestions = new List<RefactoringSuggestion>()
            });
        }
    }

    private string BuildAnalysisPrompt(string testCode, string implementationCode, int skillLevel)
    {
        var guidance = SkillLevelPromptDetail.GetRefactoringGuidance(skillLevel);

        return $@"You are a code reviewer. Identify refactoring opportunities in the code that was just written during a TDD cycle. Focus on improvements that maintain correctness while improving quality.

Tests:
```
{testCode}
```

Implementation:
```
{implementationCode}
```

{guidance}

Analyze for:
1. Code duplication
2. Naming improvements
3. Design pattern opportunities
4. Simplification possibilities
5. Performance improvements (if obvious)

Respond with JSON:
{{
    ""hasSuggestions"": true/false,
    ""confidence"": 0.0-1.0,
    ""suggestions"": [
        {{
            ""description"": ""..."",
            ""category"": ""naming|duplication|pattern|simplification|performance"",
            ""confidence"": 0.0-1.0,
            ""filePath"": ""...""
        }}
    ]
}}";
    }

    private async Task<string> CallEngineCallback(string callbackUrl, string prompt)
    {
        var httpClient = _httpClientFactory!.CreateClient();
        var requestBody = new { prompt, role = "reviewer" };
        var response = await httpClient.PostAsJsonAsync(
            $"{callbackUrl.TrimEnd('/')}/api/engine/execute-task", requestBody);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return result.GetProperty("output").GetString() ?? "{}";
    }

    private async Task<string> CallLlm(string prompt)
    {
        var httpClient = _httpClientFactory!.CreateClient("anthropic");
        var model = _configuration!["Anthropic:Model"] ?? "claude-sonnet-4-20250514";

        var requestBody = new
        {
            model,
            max_tokens = 4096,
            system = "You are a code reviewer specializing in identifying safe refactoring opportunities after a TDD green phase.",
            messages = new[] { new { role = "user", content = prompt } }
        };

        var response = await httpClient.PostAsJsonAsync("/v1/messages", requestBody);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var contentArray = result.GetProperty("content");
        foreach (var block in contentArray.EnumerateArray())
        {
            if (block.GetProperty("type").GetString() == "text")
            {
                return block.GetProperty("text").GetString() ?? "{}";
            }
        }

        return "{}";
    }

    private static string SimulateAnalysis()
    {
        var hasSuggestions = Random.Shared.Next(100) < 60;
        var confidence = hasSuggestions ? 0.6 + (Random.Shared.NextDouble() * 0.35) : 0.3;

        return JsonSerializer.Serialize(new
        {
            hasSuggestions,
            confidence,
            suggestions = hasSuggestions
                ? new[]
                {
                    new
                    {
                        description = "Extract repeated logic into a helper method",
                        category = "duplication",
                        confidence = 0.75,
                        filePath = "src/implementation.ts"
                    },
                    new
                    {
                        description = "Rename variable for clarity",
                        category = "naming",
                        confidence = 0.85,
                        filePath = "src/implementation.ts"
                    }
                }
                : Array.Empty<object>()
        });
    }

    private static RefactoringAnalysis ParseAnalysisResponse(string response, double confidenceThreshold)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(response);

            var hasSuggestions = json.TryGetProperty("hasSuggestions", out var hs) && hs.GetBoolean();
            var confidence = json.TryGetProperty("confidence", out var conf) ? conf.GetDouble() : 0;

            var suggestions = new List<RefactoringSuggestion>();
            if (json.TryGetProperty("suggestions", out var sugArray))
            {
                foreach (var sug in sugArray.EnumerateArray())
                {
                    var sugConfidence = sug.TryGetProperty("confidence", out var sc) ? sc.GetDouble() : 0;

                    suggestions.Add(new RefactoringSuggestion
                    {
                        Description = sug.TryGetProperty("description", out var desc)
                            ? desc.GetString() ?? "" : "",
                        Category = sug.TryGetProperty("category", out var cat)
                            ? cat.GetString() ?? "" : "",
                        Confidence = sugConfidence,
                        FilePath = sug.TryGetProperty("filePath", out var fp)
                            ? fp.GetString() : null
                    });
                }
            }

            // Filter suggestions by confidence threshold
            var qualifiedSuggestions = suggestions
                .Where(s => s.Confidence >= confidenceThreshold)
                .ToList();

            return new RefactoringAnalysis
            {
                HasSuggestions = hasSuggestions && qualifiedSuggestions.Count > 0,
                Confidence = confidence,
                Suggestions = qualifiedSuggestions
            };
        }
        catch (Exception)
        {
            return new RefactoringAnalysis
            {
                HasSuggestions = false,
                Confidence = 0,
                Suggestions = new List<RefactoringSuggestion>()
            };
        }
    }
}
