using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.Debug.Models;

namespace Tamma.Activities.Debug;

/// <summary>
/// Writes a regression test that reproduces the bug (BugInvestigation mode).
/// The test should FAIL initially — if it passes, the bug may already be fixed
/// or the test doesn't correctly reproduce the issue.
/// Calls LLM (role=tester) to generate the test.
/// </summary>
[Activity(
    "Tamma.Debug",
    "Write Regression Test",
    "Write test that reproduces the bug (BugInvestigation mode)",
    Kind = ActivityKind.Task
)]
public class WriteRegressionTestActivity : CodeActivity<TestGenerationResult>
{
    private readonly ILogger<WriteRegressionTestActivity>? _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    /// <summary>Session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story ID</summary>
    [Input(Description = "Story identifier")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Bug description / reproduction steps</summary>
    [Input(Description = "Bug description and reproduction steps")]
    public Input<string> BugDescription { get; set; } = default!;

    /// <summary>The hypothesis being tested</summary>
    [Input(Description = "Hypothesis to write regression test for (JSON)")]
    public Input<string> HypothesisJson { get; set; } = default!;

    /// <summary>Relevant code context</summary>
    [Input(Description = "Relevant code context")]
    public Input<string> CodeContext { get; set; } = default!;

    /// <summary>Repository URL</summary>
    [Input(Description = "Repository URL")]
    public Input<string> RepositoryUrl { get; set; } = default!;

    /// <summary>Branch name</summary>
    [Input(Description = "Branch name")]
    public Input<string> BranchName { get; set; } = default!;

    [JsonConstructor]
    public WriteRegressionTestActivity() { }

    public WriteRegressionTestActivity(
        ILogger<WriteRegressionTestActivity> logger,
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
        var storyId = StoryId.Get(context) ?? "unknown";
        var bugDescription = BugDescription.Get(context) ?? string.Empty;
        var hypothesisJson = HypothesisJson.Get(context) ?? "{}";
        var codeContext = CodeContext.Get(context) ?? string.Empty;

        _logger?.LogInformation(
            "Writing regression test for session {SessionId}, story {StoryId}",
            sessionId, storyId);

        try
        {
            var prompt = BuildTestPrompt(storyId, bugDescription, hypothesisJson, codeContext);
            var response = await CallLlm(prompt);
            var result = ParseTestResponse(response);

            if (result.Success)
            {
                _logger?.LogInformation(
                    "Regression test generated: {TestName} at {FilePath}, failsAsExpected={Fails}",
                    result.TestName, result.TestFilePath, result.FailsAsExpected);
            }
            else
            {
                _logger?.LogWarning(
                    "Failed to generate regression test for session {SessionId}: {Error}",
                    sessionId, result.ErrorMessage);
            }

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Regression test generation failed for session {SessionId}", sessionId);
            context.SetResult(new TestGenerationResult
            {
                Success = false,
                ErrorMessage = $"Test generation failed: {ex.Message}"
            });
        }
    }

    private static string BuildTestPrompt(
        string storyId, string bugDescription, string hypothesisJson, string codeContext)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("You are a test specialist (role: tester). Write a regression test that REPRODUCES this bug.");
        sb.AppendLine("The test MUST FAIL with the current buggy code — if it passes, it's not correctly reproducing the bug.");
        sb.AppendLine();
        sb.AppendLine($"## Story: {storyId}");
        sb.AppendLine();
        sb.AppendLine("## Bug Description");
        sb.AppendLine(bugDescription);
        sb.AppendLine();
        sb.AppendLine("## Root Cause Hypothesis");
        sb.AppendLine(hypothesisJson);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(codeContext))
        {
            sb.AppendLine("## Relevant Code");
            sb.AppendLine(codeContext);
            sb.AppendLine();
        }

        sb.AppendLine(@"## Required Output Format (JSON)
{
  ""test_file_path"": ""tests/regression/bug-{storyId}.test.ts"",
  ""test_name"": ""should reproduce bug #{storyId}"",
  ""test_code"": ""// test code here"",
  ""fails_as_expected"": true
}");

        return sb.ToString();
    }

    private async Task<string> CallLlm(string prompt)
    {
        var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? true;

        if (useMock)
        {
            return SimulateTestResponse();
        }

        var callbackUrl = _configuration?["Engine:CallbackUrl"];
        if (!string.IsNullOrEmpty(callbackUrl) && _httpClientFactory != null)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsJsonAsync(
                $"{callbackUrl.TrimEnd('/')}/api/engine/execute-task",
                new { prompt, analysisType = "regression_test", role = "tester" });
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            return result.GetProperty("output").GetString() ?? "{}";
        }

        return SimulateTestResponse();
    }

    private static string SimulateTestResponse()
    {
        return JsonSerializer.Serialize(new
        {
            test_file_path = "tests/regression/bug-regression.test.ts",
            test_name = "should reproduce the reported bug",
            test_code = "describe('Bug Regression', () => {\n  it('should reproduce the reported bug', () => {\n    // Arrange: set up the conditions from the bug report\n    // Act: perform the action that triggers the bug\n    // Assert: verify the buggy behavior exists\n    expect(true).toBe(true);\n  });\n});",
            fails_as_expected = true
        });
    }

    private TestGenerationResult ParseTestResponse(string response)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(response);

            return new TestGenerationResult
            {
                Success = true,
                TestFilePath = json.TryGetProperty("test_file_path", out var path)
                    ? path.GetString() ?? "" : "",
                TestName = json.TryGetProperty("test_name", out var name)
                    ? name.GetString() ?? "" : "",
                FailsAsExpected = json.TryGetProperty("fails_as_expected", out var fails)
                    && fails.GetBoolean()
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse test generation response");
            return new TestGenerationResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse response: {ex.Message}"
            };
        }
    }
}
