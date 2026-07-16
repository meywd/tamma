using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.Debug.Models;
using Tamma.Activities.LlmCall;

namespace Tamma.Activities.Debug;

/// <summary>
/// Writes a regression test that reproduces the bug (BugInvestigation mode).
/// The test should FAIL initially — if it passes, the bug may already be fixed
/// or the test doesn't correctly reproduce the issue.
///
/// <para>Story 32-5 (AC9) / completeness audit 2026-06-22 (Debugging.md §Missing #9):
/// the LLM call routes through the mediated call-LLM endpoint
/// (<see cref="MediatedLlmText"/>) with the canonical <c>tester</c> role — the engine
/// holds NO provider key and makes no direct <c>/api/engine/execute-task</c> or
/// <c>/v1/messages</c> call. There is NO simulated fallback (a fabricated
/// <c>expect(true).toBe(true)</c> test that lies about reproducing the bug is a
/// false-success in the audit trail) and a textless mediated response throws. The
/// output contract (<see cref="TestGenerationResult"/>) is unchanged.</para>
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

    public WriteRegressionTestActivity(ILogger<WriteRegressionTestActivity> logger)
    {
        _logger = logger;
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
            // Mediated call-LLM (no direct provider key in the engine). "tester" is the
            // canonical role the API resolves for test authoring. See AIDiagnosisActivity.
            var response = await MediatedLlmText.CompleteAsync(context, "tester", prompt, context.CancellationToken);
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

        sb.AppendLine("Write a regression test that REPRODUCES this bug. The test MUST FAIL with the current buggy code — if it passes, it's not correctly reproducing the bug.");
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
