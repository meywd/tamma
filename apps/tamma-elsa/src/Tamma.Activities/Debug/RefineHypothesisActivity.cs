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
/// Refines hypotheses after a failed fix attempt by calling LLM (role=debugger).
/// Passes the previous fix attempt results, test output, and updated errors to produce
/// refined or new hypotheses. This prevents the LLM from repeating failed approaches.
/// </summary>
[Activity(
    "Tamma.Debug",
    "Refine Hypothesis",
    "Update hypotheses based on failed fix attempt results",
    Kind = ActivityKind.Task
)]
public class RefineHypothesisActivity : CodeActivity<DiagnosisResult>
{
    private readonly ILogger<RefineHypothesisActivity>? _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    /// <summary>Session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>The hypothesis that was just tried</summary>
    [Input(Description = "Hypothesis that was tried (JSON)")]
    public Input<string> TriedHypothesisJson { get; set; } = default!;

    /// <summary>Test results after the fix attempt</summary>
    [Input(Description = "Test results after fix attempt")]
    public Input<string> TestResults { get; set; } = default!;

    /// <summary>New error messages (may differ after partial fix)</summary>
    [Input(Description = "Updated error messages")]
    public Input<string> UpdatedErrors { get; set; } = default!;

    /// <summary>All previous hypotheses and attempts</summary>
    [Input(Description = "Full iteration context (JSON)")]
    public Input<string> IterationContextJson { get; set; } = default!;

    [JsonConstructor]
    public RefineHypothesisActivity() { }

    public RefineHypothesisActivity(
        ILogger<RefineHypothesisActivity> logger,
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
        var triedJson = TriedHypothesisJson.Get(context) ?? "{}";
        var testResults = TestResults.Get(context) ?? string.Empty;
        var updatedErrors = UpdatedErrors.Get(context) ?? string.Empty;
        var iterationCtxJson = IterationContextJson.Get(context) ?? "{}";

        _logger?.LogInformation(
            "Refining hypotheses for session {SessionId} after failed fix",
            sessionId);

        try
        {
            var prompt = BuildRefinementPrompt(triedJson, testResults, updatedErrors, iterationCtxJson);
            var response = await CallLlm(prompt);
            var result = ParseRefinementResponse(response);

            _logger?.LogInformation(
                "Hypothesis refinement produced {Count} updated hypotheses for session {SessionId}",
                result.Hypotheses.Count, sessionId);

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Hypothesis refinement failed for session {SessionId}", sessionId);
            context.SetResult(new DiagnosisResult
            {
                AnalysisSummary = $"Refinement failed: {ex.Message}",
                Hypotheses = new List<Hypothesis>()
            });
        }
    }

    private static string BuildRefinementPrompt(
        string triedJson, string testResults, string updatedErrors, string iterationCtxJson)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("You are a debugging specialist (role: debugger). The previous fix attempt did NOT work.");
        sb.AppendLine("Refine the hypotheses based on the new information below.");
        sb.AppendLine();
        sb.AppendLine("## Hypothesis That Was Tried");
        sb.AppendLine(triedJson);
        sb.AppendLine();
        sb.AppendLine("## Test Results After Fix");
        sb.AppendLine(testResults);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(updatedErrors))
        {
            sb.AppendLine("## Updated Error Messages");
            sb.AppendLine(updatedErrors);
            sb.AppendLine();
        }

        sb.AppendLine("## Full Context of All Previous Attempts");
        sb.AppendLine(iterationCtxJson);
        sb.AppendLine();
        sb.AppendLine("## Instructions");
        sb.AppendLine("- DO NOT suggest the same fix approach that already failed");
        sb.AppendLine("- Generate NEW or REFINED hypotheses based on what we learned");
        sb.AppendLine("- If the error changed, that's useful information about what's happening");
        sb.AppendLine("- Rank new hypotheses by confidence");
        sb.AppendLine();
        sb.AppendLine(@"## Required Output Format (JSON)
{
  ""analysis_summary"": ""what we learned from the failed attempt"",
  ""hypotheses"": [
    {
      ""rank"": 1,
      ""description"": ""refined root cause"",
      ""confidence"": 0.80,
      ""suggested_fix"": ""new fix approach"",
      ""affected_files"": [""file.ts""]
    }
  ]
}");

        return sb.ToString();
    }

    private async Task<string> CallLlm(string prompt)
    {
        // No mock path: simulated refinement responses fed fake "what we learned"
        // narratives and fabricated hypotheses into the iterative debug loop,
        // poisoning subsequent attempts and the audit trail. All refinements now
        // require a real engine callback. See: feat/wave-b cleanup.
        var callbackUrl = _configuration?["Engine:CallbackUrl"];
        if (string.IsNullOrEmpty(callbackUrl) || _httpClientFactory == null)
        {
            throw new InvalidOperationException(
                "RefineHypothesisActivity requires Engine:CallbackUrl and IHttpClientFactory; "
                + "no simulated fallback is permitted.");
        }

        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"{callbackUrl.TrimEnd('/')}/api/engine/execute-task",
            new { prompt, analysisType = "debugging_refinement", role = "debugger" });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return result.GetProperty("output").GetString() ?? "{}";
    }

    private DiagnosisResult ParseRefinementResponse(string response)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(response);
            var result = new DiagnosisResult();

            if (json.TryGetProperty("analysis_summary", out var summary))
                result.AnalysisSummary = summary.GetString() ?? string.Empty;

            if (json.TryGetProperty("hypotheses", out var hypothesesArr))
            {
                foreach (var h in hypothesesArr.EnumerateArray())
                {
                    var hypothesis = new Hypothesis
                    {
                        Rank = h.TryGetProperty("rank", out var rank) ? rank.GetInt32() : 0,
                        Description = h.TryGetProperty("description", out var desc)
                            ? desc.GetString() ?? "" : "",
                        Confidence = h.TryGetProperty("confidence", out var conf)
                            ? (decimal)conf.GetDouble() : 0m,
                        SuggestedFix = h.TryGetProperty("suggested_fix", out var fix)
                            ? fix.GetString() ?? "" : "",
                        Outcome = HypothesisOutcome.Untried
                    };

                    if (h.TryGetProperty("affected_files", out var files))
                    {
                        hypothesis.AffectedFiles = files.EnumerateArray()
                            .Select(f => f.GetString() ?? "")
                            .Where(f => !string.IsNullOrEmpty(f))
                            .ToList();
                    }

                    result.Hypotheses.Add(hypothesis);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse refinement response");
            return new DiagnosisResult
            {
                AnalysisSummary = $"Failed to parse: {ex.Message}"
            };
        }
    }
}
