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
/// ELSA activity that generates the minimum implementation to make failing tests pass.
/// Part of the GREEN phase in the TDD cycle.
/// Calls LLM with role=implementer to write code that satisfies the tests.
/// </summary>
[Activity(
    "Tamma.TDD",
    "Write Implementation",
    "Generate minimum implementation to pass tests (GREEN phase)",
    Kind = ActivityKind.Task
)]
public class WriteImplementationActivity : CodeActivity<ImplementationResult>
{
    private readonly ILogger<WriteImplementationActivity>? _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story identifier</summary>
    [Input(Description = "Story identifier")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Task description from the implementation plan</summary>
    [Input(Description = "Task description")]
    public Input<string> TaskDescription { get; set; } = default!;

    /// <summary>The test code that needs to pass</summary>
    [Input(Description = "Test code that needs to pass")]
    public Input<string> TestCode { get; set; } = default!;

    /// <summary>Test failure output from the RED phase</summary>
    [Input(Description = "Test failure output from RED phase")]
    public Input<string?> TestFailureOutput { get; set; } = default!;

    /// <summary>Existing code context</summary>
    [Input(Description = "Existing code context")]
    public Input<string?> CodeContext { get; set; } = default!;

    /// <summary>Junior developer's skill level (1-5)</summary>
    [Input(Description = "Junior skill level (1-5)", DefaultValue = 3)]
    public Input<int> SkillLevel { get; set; } = new(3);

    [JsonConstructor]
    public WriteImplementationActivity() { }

    public WriteImplementationActivity(
        ILogger<WriteImplementationActivity> logger,
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
        var taskDescription = TaskDescription.Get(context);
        var testCode = TestCode.Get(context);
        var testFailureOutput = TestFailureOutput.Get(context);
        var codeContext = CodeContext.Get(context);
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);

        _logger?.LogInformation(
            "TDD GREEN phase: Writing implementation for task in story {StoryId}, session {SessionId}",
            storyId, sessionId);

        try
        {
            var prompt = BuildImplementationPrompt(taskDescription, testCode, testFailureOutput, codeContext, skillLevel);

            var callbackUrl = _configuration?["Engine:CallbackUrl"];
            var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? false;

            string response;
            if (useMock)
            {
                response = SimulateImplementation(taskDescription);
            }
            else if (!string.IsNullOrEmpty(callbackUrl))
            {
                response = await CallEngineCallback(callbackUrl, prompt);
            }
            else
            {
                response = await CallLlm(prompt);
            }

            var result = ParseImplementationResponse(response);

            _logger?.LogInformation(
                "TDD GREEN phase: Generated implementation across {FileCount} files for session {SessionId}",
                result.ImplementationFiles.Count, sessionId);

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TDD GREEN phase: Error generating implementation for session {SessionId}", sessionId);

            context.SetResult(new ImplementationResult
            {
                Success = false,
                ErrorMessage = $"Implementation generation failed: {ex.Message}"
            });
        }
    }

    private string BuildImplementationPrompt(
        string taskDescription,
        string testCode,
        string? testFailureOutput,
        string? codeContext,
        int skillLevel)
    {
        var guidance = SkillLevelPromptDetail.GetImplementationGuidance(skillLevel);
        var failureSection = !string.IsNullOrEmpty(testFailureOutput)
            ? $"\n\nTest failure output:\n```\n{testFailureOutput}\n```"
            : "";
        var contextSection = !string.IsNullOrEmpty(codeContext)
            ? $"\n\nExisting code context:\n{codeContext}"
            : "";

        return $@"You are a TDD implementer. Write the MINIMUM implementation needed to make ALL the following tests pass. Do not over-engineer — write just enough code to satisfy the tests.

Task: {taskDescription}

Tests to satisfy:
```
{testCode}
```
{failureSection}
{contextSection}

{guidance}

Requirements:
1. Write the minimum code to make all tests pass
2. Do not break any existing tests
3. Follow the project's coding conventions
4. Keep the implementation simple and focused

Respond with JSON: {{""implementationCode"": ""..."", ""implementationFiles"": [""...""]}}";
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
            system = "You are a TDD implementer. Write the minimum code needed to make failing tests pass. Keep it simple and focused.",
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

    private static string SimulateImplementation(string taskDescription)
    {
        return JsonSerializer.Serialize(new
        {
            implementationCode = $"// Implementation for: {taskDescription}\n" +
                                 "export function implementation() {\n" +
                                 "  // Minimum implementation to pass tests\n" +
                                 "  return true;\n" +
                                 "}",
            implementationFiles = new[] { "src/implementation.ts" }
        });
    }

    private static ImplementationResult ParseImplementationResponse(string response)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(response);

            var code = json.TryGetProperty("implementationCode", out var ic)
                ? ic.GetString() ?? ""
                : "";
            var files = json.TryGetProperty("implementationFiles", out var imf)
                ? JsonSerializer.Deserialize<List<string>>(imf.GetRawText()) ?? new List<string>()
                : new List<string>();

            return new ImplementationResult
            {
                Success = !string.IsNullOrEmpty(code),
                ImplementationCode = code,
                ImplementationFiles = files
            };
        }
        catch (Exception ex)
        {
            return new ImplementationResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse implementation response: {ex.Message}"
            };
        }
    }
}
