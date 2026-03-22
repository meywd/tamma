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
/// ELSA activity that generates failing tests for a task using an LLM call.
/// Part of the RED phase in the TDD cycle.
/// Calls LLM with role=tester to write tests that should initially fail.
/// Skill-level adaptation: L1-2 get detailed templates, L4-5 get high-level specs.
/// </summary>
[Activity(
    "Tamma.TDD",
    "Write Tests",
    "Generate failing tests for the task using LLM (RED phase)",
    Kind = ActivityKind.Task
)]
public class WriteTestsActivity : CodeActivity<TestGenerationResult>
{
    private readonly ILogger<WriteTestsActivity>? _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story identifier</summary>
    [Input(Description = "Story identifier")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Task description from the implementation plan</summary>
    [Input(Description = "Task description from the implementation plan")]
    public Input<string> TaskDescription { get; set; } = default!;

    /// <summary>Files relevant to this task</summary>
    [Input(Description = "Files relevant to this task")]
    public Input<List<string>> TaskFiles { get; set; } = default!;

    /// <summary>Existing code context gathered from Context Gathering workflow</summary>
    [Input(Description = "Existing code context")]
    public Input<string?> CodeContext { get; set; } = default!;

    /// <summary>Junior developer's skill level (1-5)</summary>
    [Input(Description = "Junior skill level (1-5)", DefaultValue = 3)]
    public Input<int> SkillLevel { get; set; } = new(3);

    /// <summary>Whether this is a rewrite attempt (tests passed when they should fail)</summary>
    [Input(Description = "Whether this is a rewrite attempt", DefaultValue = false)]
    public Input<bool> IsRewrite { get; set; } = new(false);

    /// <summary>Previous test code that incorrectly passed (for rewrite context)</summary>
    [Input(Description = "Previous test code that passed (for rewrites)")]
    public Input<string?> PreviousTestCode { get; set; } = default!;

    [JsonConstructor]
    public WriteTestsActivity() { }

    public WriteTestsActivity(
        ILogger<WriteTestsActivity> logger,
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
        var taskFiles = TaskFiles.Get(context) ?? new List<string>();
        var codeContext = CodeContext.Get(context);
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);
        var isRewrite = IsRewrite.Get(context);
        var previousTestCode = PreviousTestCode.Get(context);

        _logger?.LogInformation(
            "TDD RED phase: Writing {Action} tests for task in story {StoryId}, session {SessionId}, skill level {SkillLevel}",
            isRewrite ? "rewrite" : "initial", storyId, sessionId, skillLevel);

        try
        {
            var prompt = BuildTestPrompt(taskDescription, taskFiles, codeContext, skillLevel, isRewrite, previousTestCode);

            var callbackUrl = _configuration?["Engine:CallbackUrl"];
            var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? false;

            string response;
            if (useMock)
            {
                response = SimulateTestGeneration(taskDescription, taskFiles, isRewrite);
            }
            else if (!string.IsNullOrEmpty(callbackUrl))
            {
                response = await CallEngineCallback(callbackUrl, prompt);
            }
            else
            {
                response = await CallLlm(prompt);
            }

            var result = ParseTestGenerationResponse(response, taskFiles);

            _logger?.LogInformation(
                "TDD RED phase: Generated {TestCount} tests across {FileCount} files for session {SessionId}",
                result.TestCount, result.TestFiles.Count, sessionId);

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TDD RED phase: Error generating tests for session {SessionId}", sessionId);

            context.SetResult(new TestGenerationResult
            {
                Success = false,
                ErrorMessage = $"Test generation failed: {ex.Message}"
            });
        }
    }

    private string BuildTestPrompt(
        string taskDescription,
        List<string> taskFiles,
        string? codeContext,
        int skillLevel,
        bool isRewrite,
        string? previousTestCode)
    {
        var guidance = SkillLevelPromptDetail.GetTestPromptGuidance(skillLevel);
        var filesSection = taskFiles.Count > 0
            ? $"\n\nRelevant files:\n{string.Join("\n", taskFiles.Select(f => $"- {f}"))}"
            : "";
        var contextSection = !string.IsNullOrEmpty(codeContext)
            ? $"\n\nExisting code context:\n{codeContext}"
            : "";

        if (isRewrite && !string.IsNullOrEmpty(previousTestCode))
        {
            return $@"You are a TDD tester. The following tests were written but they PASS without any implementation, meaning they don't actually test the new behavior. Rewrite them to genuinely test the NEW functionality that needs to be implemented.

Task: {taskDescription}
{filesSection}
{contextSection}

Previous tests that incorrectly passed:
```
{previousTestCode}
```

Rewrite these tests so they:
1. Actually test the new behavior described in the task
2. Will FAIL until the implementation is written
3. Are meaningful and cover edge cases

{guidance}

Respond with JSON: {{""testCode"": ""..."", ""testFiles"": [""...""], ""testCount"": N}}";
        }

        return $@"You are a TDD tester. Write failing tests for the following task. These tests MUST fail initially because the implementation does not exist yet.

Task: {taskDescription}
{filesSection}
{contextSection}

{guidance}

Write tests that:
1. Cover the happy path for the task
2. Cover edge cases and error conditions
3. Will FAIL until the implementation is written
4. Follow the project's existing test patterns

Respond with JSON: {{""testCode"": ""..."", ""testFiles"": [""...""], ""testCount"": N}}";
    }

    private async Task<string> CallEngineCallback(string callbackUrl, string prompt)
    {
        var httpClient = _httpClientFactory!.CreateClient();
        var requestBody = new { prompt, role = "tester" };
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
            system = "You are a TDD test writer. Generate tests that will fail initially and only pass once the correct implementation is in place.",
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

    private static string SimulateTestGeneration(string taskDescription, List<string> taskFiles, bool isRewrite)
    {
        var testFile = taskFiles.Count > 0
            ? taskFiles[0].Replace(".ts", ".test.ts").Replace(".cs", ".Tests.cs")
            : "tests/generated-test.test.ts";

        return JsonSerializer.Serialize(new
        {
            testCode = $"// {(isRewrite ? "Rewritten" : "Generated")} tests for: {taskDescription}\n" +
                       "describe('Task Tests', () => {\n" +
                       "  it('should implement the required behavior', () => {\n" +
                       "    // This test should FAIL until implementation exists\n" +
                       "    expect(true).toBe(false);\n" +
                       "  });\n" +
                       "  it('should handle edge cases', () => {\n" +
                       "    expect(true).toBe(false);\n" +
                       "  });\n" +
                       "});",
            testFiles = new[] { testFile },
            testCount = 2
        });
    }

    private static TestGenerationResult ParseTestGenerationResponse(string response, List<string> fallbackFiles)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(response);

            var testCode = json.TryGetProperty("testCode", out var tc)
                ? tc.GetString() ?? ""
                : "";
            var testFiles = json.TryGetProperty("testFiles", out var tf)
                ? JsonSerializer.Deserialize<List<string>>(tf.GetRawText()) ?? new List<string>()
                : new List<string>();
            var testCount = json.TryGetProperty("testCount", out var tcount)
                ? tcount.GetInt32()
                : 0;

            return new TestGenerationResult
            {
                Success = !string.IsNullOrEmpty(testCode),
                TestCode = testCode,
                TestFiles = testFiles,
                TestCount = testCount
            };
        }
        catch (Exception ex)
        {
            return new TestGenerationResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse test generation response: {ex.Message}"
            };
        }
    }
}
