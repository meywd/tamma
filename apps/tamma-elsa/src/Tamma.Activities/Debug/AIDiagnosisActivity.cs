using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.Debug.Models;
using Tamma.Activities.LlmCall;

namespace Tamma.Activities.Debug;

/// <summary>
/// AI diagnosis activity: calls LLM (role=debugger) to generate ranked root cause hypotheses.
/// Sends all gathered debug context plus previous failed attempts (if retrying) to the LLM.
/// Returns a DiagnosisResult with ranked hypotheses.
///
/// <para>Story 32-5 (AC9): the LLM call routes through the mediated call-LLM
/// endpoint (<see cref="MediatedLlmText"/>) — the engine holds NO provider key
/// and makes no direct <c>/v1/messages</c> call. There is NO simulated path (a
/// fabricated diagnosis would poison the audit trail / debug loop), and a textless
/// mediated response throws rather than fabricating. The output contract
/// (<see cref="DiagnosisResult"/>) is unchanged.</para>
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
            // Role MUST be a canonical AgentRole wire (or a RolePhaseMap alias) — the
            // API's AgentResolverService runs AssertValidRole and 422s on an unknown
            // role. Diagnosis is a senior analytical task, so it resolves the
            // senior_developer agent/prompt (Epic-27). The free-text "debugger" label
            // is NOT a valid role and was rejected on every non-mock call.
            var response = await MediatedLlmText.CompleteAsync(context, "senior_developer", prompt, context.CancellationToken);
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
