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
/// ELSA activity that applies suggested refactoring changes to the implementation code.
/// Part of the REFACTOR phase in the TDD cycle.
/// Calls LLM with role=implementer to apply the reviewer's suggestions.
/// After this activity, tests must be re-run to verify the refactoring didn't break anything.
/// </summary>
[Activity(
    "Tamma.TDD",
    "Apply Refactoring",
    "Apply suggested refactoring to the implementation (REFACTOR phase)",
    Kind = ActivityKind.Task
)]
public class ApplyRefactoringActivity : CodeActivity<RefactoringResult>
{
    private readonly ILogger<ApplyRefactoringActivity>? _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story identifier</summary>
    [Input(Description = "Story identifier")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Current implementation code to refactor</summary>
    [Input(Description = "Current implementation code")]
    public Input<string> ImplementationCode { get; set; } = default!;

    /// <summary>Test code to ensure compatibility</summary>
    [Input(Description = "Test code for reference")]
    public Input<string> TestCode { get; set; } = default!;

    /// <summary>Refactoring suggestions to apply</summary>
    [Input(Description = "Refactoring suggestions to apply")]
    public Input<List<RefactoringSuggestion>> Suggestions { get; set; } = default!;

    /// <summary>Junior developer's skill level (1-5)</summary>
    [Input(Description = "Junior skill level (1-5)", DefaultValue = 3)]
    public Input<int> SkillLevel { get; set; } = new(3);

    [JsonConstructor]
    public ApplyRefactoringActivity() { }

    public ApplyRefactoringActivity(
        ILogger<ApplyRefactoringActivity> logger,
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
        var implementationCode = ImplementationCode.Get(context);
        var testCode = TestCode.Get(context);
        var suggestions = Suggestions.Get(context) ?? new List<RefactoringSuggestion>();
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);

        _logger?.LogInformation(
            "TDD REFACTOR phase: Applying {SuggestionCount} refactoring suggestions for story {StoryId}, session {SessionId}",
            suggestions.Count, storyId, sessionId);

        try
        {
            var prompt = BuildRefactoringPrompt(implementationCode, testCode, suggestions, skillLevel);

            var callbackUrl = _configuration?["Engine:CallbackUrl"];
            var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? false;

            string response;
            if (useMock)
            {
                response = SimulateRefactoring(implementationCode);
            }
            else if (!string.IsNullOrEmpty(callbackUrl))
            {
                response = await CallEngineCallback(callbackUrl, prompt);
            }
            else
            {
                response = await CallLlm(prompt);
            }

            var result = ParseRefactoringResponse(response);

            _logger?.LogInformation(
                "TDD REFACTOR phase: Applied refactoring to {FileCount} files for session {SessionId}",
                result.FilesChanged.Count, sessionId);

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TDD REFACTOR phase: Error applying refactoring for session {SessionId}", sessionId);

            context.SetResult(new RefactoringResult
            {
                Success = false,
                ErrorMessage = $"Refactoring failed: {ex.Message}"
            });
        }
    }

    private static string BuildRefactoringPrompt(
        string implementationCode,
        string testCode,
        List<RefactoringSuggestion> suggestions,
        int skillLevel)
    {
        var suggestionsText = string.Join("\n", suggestions.Select((s, i) =>
            $"{i + 1}. [{s.Category}] {s.Description} (confidence: {s.Confidence:F2})"));

        var guidance = SkillLevelPromptDetail.GetRefactoringGuidance(skillLevel);

        return $@"You are a TDD implementer applying refactoring. Apply the following refactoring suggestions to the implementation code. The refactored code MUST still pass all existing tests.

Current implementation:
```
{implementationCode}
```

Tests that must continue to pass:
```
{testCode}
```

Refactoring suggestions to apply:
{suggestionsText}

{guidance}

Requirements:
1. Apply the suggested refactorings
2. Maintain all existing functionality (tests must still pass)
3. Do not change test files
4. Keep changes minimal and focused

Respond with JSON: {{""refactoredCode"": ""..."", ""filesChanged"": [""...""]}}";
    }

    private async Task<string> CallEngineCallback(string callbackUrl, string prompt)
    {
        var httpClient = _httpClientFactory!.CreateClient();
        var requestBody = new { prompt, role = "implementer" };
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
            system = "You are a TDD implementer applying safe refactorings. All existing tests must continue to pass after your changes.",
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

    private static string SimulateRefactoring(string originalCode)
    {
        return JsonSerializer.Serialize(new
        {
            refactoredCode = $"// Refactored version\n{originalCode}\n// (refactoring applied: extracted helper, improved naming)",
            filesChanged = new[] { "src/implementation.ts" }
        });
    }

    private static RefactoringResult ParseRefactoringResponse(string response)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(response);

            var code = json.TryGetProperty("refactoredCode", out var rc)
                ? rc.GetString() ?? ""
                : "";
            var files = json.TryGetProperty("filesChanged", out var fc)
                ? JsonSerializer.Deserialize<List<string>>(fc.GetRawText()) ?? new List<string>()
                : new List<string>();

            return new RefactoringResult
            {
                Success = !string.IsNullOrEmpty(code),
                RefactoredCode = code,
                FilesChanged = files
            };
        }
        catch (Exception ex)
        {
            return new RefactoringResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse refactoring response: {ex.Message}"
            };
        }
    }
}
