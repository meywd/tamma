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
/// AI diagnosis activity: calls LLM (role=debugger) to generate ranked root cause hypotheses.
/// Sends all gathered debug context plus previous failed attempts (if retrying) to the LLM.
/// Returns a DiagnosisResult with ranked hypotheses.
/// </summary>
[Activity(
    "Tamma.Debug",
    "AI Diagnosis",
    "Generate ranked root cause hypotheses using AI debugger",
    Kind = ActivityKind.Task
)]
public class AIDiagnosisActivity : CodeActivity<DiagnosisResult>
{
    private readonly ILogger<AIDiagnosisActivity>? _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    /// <summary>Session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Debug context mode</summary>
    [Input(Description = "Debug context mode")]
    public Input<string> DebugContextMode { get; set; } = default!;

    /// <summary>Collected error messages</summary>
    [Input(Description = "Collected error messages")]
    public Input<string> ErrorContext { get; set; } = default!;

    /// <summary>Collected relevant code context</summary>
    [Input(Description = "Collected relevant code")]
    public Input<string> CodeContext { get; set; } = default!;

    /// <summary>Collected git history</summary>
    [Input(Description = "Collected git history")]
    public Input<string> GitContext { get; set; } = default!;

    /// <summary>Collected test results</summary>
    [Input(Description = "Collected test results")]
    public Input<string> TestContext { get; set; } = default!;

    /// <summary>Reproduction steps (BugInvestigation only)</summary>
    [Input(Description = "Reproduction steps")]
    public Input<string> ReproductionContext { get; set; } = default!;

    /// <summary>Previous failed attempts (for context accumulation)</summary>
    [Input(Description = "Previous iteration context (JSON)")]
    public Input<string?> PreviousContext { get; set; } = default!;

    /// <summary>Skill level of the junior developer</summary>
    [Input(Description = "Junior skill level (1-5)", DefaultValue = 3)]
    public Input<int> SkillLevel { get; set; } = new(3);

    [JsonConstructor]
    public AIDiagnosisActivity() { }

    public AIDiagnosisActivity(
        ILogger<AIDiagnosisActivity> logger,
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
        var mode = DebugContextMode.Get(context);
        var errorCtx = ErrorContext.Get(context) ?? string.Empty;
        var codeCtx = CodeContext.Get(context) ?? string.Empty;
        var gitCtx = GitContext.Get(context) ?? string.Empty;
        var testCtx = TestContext.Get(context) ?? string.Empty;
        var reproCtx = ReproductionContext.Get(context) ?? string.Empty;
        var previousCtx = PreviousContext.Get(context);
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);

        _logger?.LogInformation(
            "Running AI diagnosis for session {SessionId}, mode={Mode}",
            sessionId, mode);

        try
        {
            var prompt = BuildDiagnosisPrompt(mode, errorCtx, codeCtx, gitCtx, testCtx, reproCtx, previousCtx);
            var response = await CallLlm(prompt);
            var result = ParseDiagnosisResponse(response);

            _logger?.LogInformation(
                "AI diagnosis generated {Count} hypotheses for session {SessionId}",
                result.Hypotheses.Count, sessionId);

            // Log each hypothesis
            foreach (var h in result.Hypotheses)
            {
                _logger?.LogInformation(
                    "Hypothesis #{Rank}: {Description} (confidence={Confidence:F2})",
                    h.Rank, h.Description, h.Confidence);
            }

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AI diagnosis failed for session {SessionId}", sessionId);

            // Return a fallback hypothesis
            context.SetResult(new DiagnosisResult
            {
                AnalysisSummary = $"AI diagnosis failed: {ex.Message}",
                Hypotheses = new List<Hypothesis>
                {
                    new Hypothesis
                    {
                        Rank = 1,
                        Description = "Unable to generate hypothesis — AI diagnosis failed",
                        Confidence = 0.1m,
                        SuggestedFix = "Manual investigation required"
                    }
                }
            });
        }
    }

    private string BuildDiagnosisPrompt(
        string mode, string errorCtx, string codeCtx, string gitCtx,
        string testCtx, string reproCtx, string? previousCtx)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("You are a debugging specialist (role: debugger). Analyze the following context and generate ranked root cause hypotheses.");
        sb.AppendLine();
        sb.AppendLine($"## Debug Mode: {mode}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(errorCtx))
        {
            sb.AppendLine("## Error Messages / Stack Traces");
            sb.AppendLine(errorCtx);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(codeCtx))
        {
            sb.AppendLine("## Relevant Code");
            sb.AppendLine(codeCtx);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(gitCtx))
        {
            sb.AppendLine("## Git History");
            sb.AppendLine(gitCtx);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(testCtx))
        {
            sb.AppendLine("## Test Results");
            sb.AppendLine(testCtx);
            sb.AppendLine();
        }

        if (mode == "BugInvestigation" && !string.IsNullOrWhiteSpace(reproCtx))
        {
            sb.AppendLine("## Reproduction Steps");
            sb.AppendLine(reproCtx);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(previousCtx))
        {
            sb.AppendLine("## IMPORTANT: Previous Failed Attempts");
            sb.AppendLine("The following fix attempts have already been tried and FAILED. Do NOT suggest the same approaches again.");
            sb.AppendLine(previousCtx);
            sb.AppendLine();
        }

        sb.AppendLine("## Required Output Format (JSON)");
        sb.AppendLine(@"{
  ""analysis_summary"": ""brief summary of the analysis"",
  ""hypotheses"": [
    {
      ""rank"": 1,
      ""description"": ""root cause description"",
      ""confidence"": 0.85,
      ""suggested_fix"": ""how to fix it"",
      ""affected_files"": [""file1.ts"", ""file2.ts""]
    }
  ]
}");

        return sb.ToString();
    }

    private async Task<string> CallLlm(string prompt)
    {
        var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? true;

        if (useMock)
        {
            return SimulateDiagnosisResponse();
        }

        var callbackUrl = _configuration?["Engine:CallbackUrl"];
        if (!string.IsNullOrEmpty(callbackUrl) && _httpClientFactory != null)
        {
            var client = _httpClientFactory.CreateClient();
            var requestBody = new
            {
                prompt,
                analysisType = "debugging_diagnosis",
                role = "debugger"
            };

            var response = await client.PostAsJsonAsync(
                $"{callbackUrl.TrimEnd('/')}/api/engine/execute-task", requestBody);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            return result.GetProperty("output").GetString() ?? "{}";
        }

        // Direct API call
        if (_httpClientFactory != null)
        {
            var client = _httpClientFactory.CreateClient("anthropic");
            var model = _configuration?["Anthropic:Model"] ?? "claude-sonnet-4-20250514";

            var requestBody = new
            {
                model,
                max_tokens = 4096,
                system = "You are an expert debugging specialist. Analyze the provided context and generate ranked root cause hypotheses in JSON format.",
                messages = new[] { new { role = "user", content = prompt } }
            };

            var response = await client.PostAsJsonAsync("/v1/messages", requestBody);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            var contentArray = result.GetProperty("content");
            foreach (var block in contentArray.EnumerateArray())
            {
                if (block.GetProperty("type").GetString() == "text")
                    return block.GetProperty("text").GetString() ?? "{}";
            }
        }

        return SimulateDiagnosisResponse();
    }

    private static string SimulateDiagnosisResponse()
    {
        return JsonSerializer.Serialize(new
        {
            analysis_summary = "Analysis of error context suggests multiple potential root causes. " +
                "The most likely cause is a logic error in the implementation.",
            hypotheses = new[]
            {
                new
                {
                    rank = 1,
                    description = "Logic error in condition evaluation — incorrect operator or boundary check",
                    confidence = 0.75,
                    suggested_fix = "Review and correct the conditional logic in the failing path",
                    affected_files = new[] { "src/main.ts" }
                },
                new
                {
                    rank = 2,
                    description = "Missing null/undefined check causing runtime exception",
                    confidence = 0.55,
                    suggested_fix = "Add null guard before accessing the property",
                    affected_files = new[] { "src/service.ts" }
                },
                new
                {
                    rank = 3,
                    description = "Type mismatch between expected and actual function return",
                    confidence = 0.35,
                    suggested_fix = "Ensure function return type matches the caller's expectation",
                    affected_files = new[] { "src/types.ts", "src/handler.ts" }
                }
            }
        });
    }

    private DiagnosisResult ParseDiagnosisResponse(string response)
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

            // Ensure hypotheses are ranked
            if (result.Hypotheses.Count > 0 && result.Hypotheses.All(h => h.Rank == 0))
            {
                for (var i = 0; i < result.Hypotheses.Count; i++)
                    result.Hypotheses[i].Rank = i + 1;
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse AI diagnosis response");
            return new DiagnosisResult
            {
                AnalysisSummary = $"Failed to parse response: {ex.Message}",
                Hypotheses = new List<Hypothesis>
                {
                    new Hypothesis
                    {
                        Rank = 1,
                        Description = "Parse failure — raw response available for manual review",
                        Confidence = 0.1m,
                        SuggestedFix = "Review raw LLM output"
                    }
                }
            };
        }
    }
}
