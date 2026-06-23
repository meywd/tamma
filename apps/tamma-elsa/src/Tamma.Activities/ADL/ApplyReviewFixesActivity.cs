using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL.Models;
using Tamma.Activities.LlmCall;

namespace Tamma.Activities.ADL;

/// <summary>
/// Applies fixes for review comments using AI-generated code changes.
/// Generates fixes based on review comments, then returns the result.
///
/// <para>Story 32-5 (AC9): when the parent workflow does not pre-supply a fix
/// response (the DispatchWorkflow llm-call path via <see cref="LlmFixResponse"/>),
/// the internal LLM call routes through the mediated call-LLM endpoint
/// (<see cref="MediatedLlmText"/>) — the engine holds NO provider key. The mock
/// path remains for tests; the output contract (<see cref="ReviewFixResult"/>) is
/// unchanged.</para>
///
/// Outcomes:
///   - Fixed: fixes generated successfully
///   - Error: fix generation failed
/// </summary>
[Activity(
    "Tamma.ADL",
    "Apply Review Fixes",
    "Generate and apply fixes for PR review comments via AI",
    Kind = ActivityKind.Task
)]
[FlowNode("Fixed", "Error")]
public class ApplyReviewFixesActivity : Activity
{
    private readonly ILogger<ApplyReviewFixesActivity>? _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Review analysis JSON with fix items")]
    public Input<string> AnalysisJson { get; set; } = default!;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Branch name for commits")]
    public Input<string> BranchName { get; set; } = default!;

    [Input(Description = "LLM-generated fix response from the dispatched llm-call workflow (optional — if provided, skips internal LLM call)")]
    public Input<string?> LlmFixResponse { get; set; } = default!;

    [Output(Description = "Whether fixes were successfully applied")]
    public Output<bool> FixesApplied { get; set; } = default!;

    [Output(Description = "Structured result of the fix operation")]
    public Output<ReviewFixResult?> FixResult { get; set; } = default!;

    [JsonConstructor]
    public ApplyReviewFixesActivity() { }

    public ApplyReviewFixesActivity(ILogger<ApplyReviewFixesActivity> logger)
    {
        _logger = logger;
    }

    public ApplyReviewFixesActivity(
        ILogger<ApplyReviewFixesActivity> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var analysisJson = AnalysisJson.Get(context);
        var repository = Repository.Get(context);
        var branchName = BranchName.Get(context);
        var externalLlmResponse = LlmFixResponse.Get(context);

        try
        {
            var analysis = DeserializeAnalysis(analysisJson);
            if (analysis == null || analysis.FixItems.Count == 0)
            {
                _logger?.LogInformation("No fix items found in analysis for {Repo}", repository);
                var emptyResult = new ReviewFixResult { Success = true };
                FixesApplied.Set(context, true);
                FixResult.Set(context, emptyResult);
                await context.CompleteActivityWithOutcomesAsync("Fixed");
                return;
            }

            _logger?.LogInformation(
                "Applying review fixes on branch {Branch} in {Repo}: {Count} fix items",
                branchName, repository, analysis.FixItems.Count);

            string response;
            if (!string.IsNullOrEmpty(externalLlmResponse))
            {
                // Response was provided by the parent workflow's DispatchWorkflow (llm-call)
                response = externalLlmResponse;
            }
            else
            {
                // Generate fixes internally (mock for tests, else mediated call-LLM)
                var prompt = BuildFixPrompt(analysis, repository, branchName);
                var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? false;

                response = useMock
                    ? SimulateFixGeneration(analysis)
                    : await MediatedLlmText.CompleteAsync(context, "implementer", prompt, context.CancellationToken);
            }

            var result = ParseFixResponse(response, analysis);

            _logger?.LogInformation(
                "Review fixes generated: {FileCount} files fixed, success={Success}",
                result.FilesFixed.Count, result.Success);

            FixesApplied.Set(context, result.Success);
            FixResult.Set(context, result);
            await context.CompleteActivityWithOutcomesAsync("Fixed");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error applying review fixes on branch {Branch} in {Repo}",
                branchName, repository);

            var errorResult = new ReviewFixResult
            {
                Success = false,
                ErrorMessage = $"Fix generation failed: {ex.Message}"
            };
            FixesApplied.Set(context, false);
            FixResult.Set(context, errorResult);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }

    internal static string BuildFixPrompt(ReviewAnalysisResult analysis, string repository, string branchName)
    {
        var commentSections = new System.Text.StringBuilder();
        for (var i = 0; i < analysis.FixItems.Count; i++)
        {
            var item = analysis.FixItems[i];
            commentSections.AppendLine($"### Comment {i + 1} [{item.Category}] (Priority: {item.Priority})");
            commentSections.AppendLine($"- File: {item.FilePath}");
            if (item.Line.HasValue)
                commentSections.AppendLine($"- Line: {item.Line}");
            commentSections.AppendLine($"- Comment: {item.Comment}");
            if (!string.IsNullOrEmpty(item.SuggestedFix))
                commentSections.AppendLine($"- Suggested fix: {item.SuggestedFix}");
            commentSections.AppendLine();
        }

        return $@"You are a code reviewer fix assistant. You need to apply fixes for the following review comments on repository {repository}, branch {branchName}.

## Review Comments to Address

{commentSections}

## Instructions

1. For each review comment above, generate the corrected code
2. Maintain all existing functionality — only fix what each comment asks for
3. If a comment is a question, add a code comment explaining the answer
4. If a comment is praise, skip it (no changes needed)
5. Prioritize bug fixes and security issues over style changes
6. Keep fixes minimal and focused — do not refactor unrelated code

## Response Format

Respond with valid JSON:
{{
  ""fixedCode"": ""<the complete fixed code for all files, with file markers>"",
  ""filesFixed"": [""path/to/file1.ts"", ""path/to/file2.ts""],
  ""fixDescriptions"": [
    {{
      ""filePath"": ""path/to/file.ts"",
      ""originalComment"": ""the review comment"",
      ""fixApplied"": ""description of what was fixed"",
      ""line"": 42
    }}
  ]
}}";
    }

    internal static string SimulateFixGeneration(ReviewAnalysisResult analysis)
    {
        var filesFixed = analysis.FixItems
            .Where(f => ReviewCommentCategory.IsActionable(f.Category))
            .Select(f => f.FilePath)
            .Distinct()
            .ToList();

        var fixDescriptions = analysis.FixItems
            .Where(f => ReviewCommentCategory.IsActionable(f.Category))
            .Select(f => new
            {
                filePath = f.FilePath,
                originalComment = f.Comment,
                fixApplied = $"Applied fix for: {f.Comment}",
                line = f.Line
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            fixedCode = "// Mock: fixes applied for review comments",
            filesFixed,
            fixDescriptions
        });
    }

    internal static ReviewFixResult ParseFixResponse(string response, ReviewAnalysisResult analysis)
    {
        try
        {
            // Try to extract JSON from markdown code fences if present
            var jsonStr = ExtractJson(response);
            var json = JsonSerializer.Deserialize<JsonElement>(jsonStr);

            var fixedCode = json.TryGetProperty("fixedCode", out var fc)
                ? fc.GetString() ?? ""
                : "";
            var filesFixed = json.TryGetProperty("filesFixed", out var ff)
                ? JsonSerializer.Deserialize<List<string>>(ff.GetRawText()) ?? new List<string>()
                : new List<string>();
            var fixDescriptions = json.TryGetProperty("fixDescriptions", out var fd)
                ? ParseFixDescriptions(fd)
                : new List<ReviewFixDescription>();

            return new ReviewFixResult
            {
                Success = filesFixed.Count > 0 || !string.IsNullOrEmpty(fixedCode),
                FixedCode = fixedCode,
                FilesFixed = filesFixed,
                FixDescriptions = fixDescriptions
            };
        }
        catch (Exception ex)
        {
            return new ReviewFixResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse fix response: {ex.Message}"
            };
        }
    }

    private static List<ReviewFixDescription> ParseFixDescriptions(JsonElement element)
    {
        var descriptions = new List<ReviewFixDescription>();
        foreach (var item in element.EnumerateArray())
        {
            var desc = new ReviewFixDescription
            {
                FilePath = item.TryGetProperty("filePath", out var fp) ? fp.GetString() ?? "" : "",
                OriginalComment = item.TryGetProperty("originalComment", out var oc) ? oc.GetString() ?? "" : "",
                FixApplied = item.TryGetProperty("fixApplied", out var fa) ? fa.GetString() ?? "" : "",
                Line = item.TryGetProperty("line", out var ln) && ln.ValueKind == JsonValueKind.Number ? ln.GetInt32() : null
            };
            descriptions.Add(desc);
        }
        return descriptions;
    }

    private static string ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "{}";

        // Strip markdown code fences: ```json ... ``` or ``` ... ```
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
                trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```"))
                trimmed = trimmed[..^3].TrimEnd();
        }

        return trimmed;
    }

    private static ReviewAnalysisResult? DeserializeAnalysis(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ReviewAnalysisResult>(json);
        }
        catch
        {
            return null;
        }
    }
}
