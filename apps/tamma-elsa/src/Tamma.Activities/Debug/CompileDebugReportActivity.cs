using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.Debug.Models;

namespace Tamma.Activities.Debug;

/// <summary>
/// Compiles a comprehensive debug report when max iterations are reached without resolution.
/// The report includes all hypotheses, fix attempts, remaining failures, and suggested next steps.
/// This report is intended for a human developer to continue debugging.
/// </summary>
[Activity(
    "Tamma.Debug",
    "Compile Debug Report",
    "Generate comprehensive report for escalation with all hypotheses and attempts",
    Kind = ActivityKind.Task
)]
public class CompileDebugReportActivity : CodeActivity<DebugReport>
{
    private readonly ILogger<CompileDebugReportActivity>? _logger;

    /// <summary>Session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story ID</summary>
    [Input(Description = "Story identifier")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Debug context mode</summary>
    [Input(Description = "Debug context mode")]
    public Input<string> DebugContextMode { get; set; } = default!;

    /// <summary>All hypotheses as JSON</summary>
    [Input(Description = "All hypotheses (JSON)")]
    public Input<string> HypothesesJson { get; set; } = default!;

    /// <summary>All fix attempts as JSON</summary>
    [Input(Description = "All fix attempts (JSON)")]
    public Input<string> AttemptsJson { get; set; } = default!;

    /// <summary>Remaining test failures</summary>
    [Input(Description = "Remaining test failures")]
    public Input<string> RemainingFailures { get; set; } = default!;

    /// <summary>Files investigated during debugging</summary>
    [Input(Description = "Files investigated")]
    public Input<string> FilesInvestigated { get; set; } = default!;

    /// <summary>Debug session start time (ISO 8601)</summary>
    [Input(Description = "Debug session start time")]
    public Input<string> StartTime { get; set; } = default!;

    [JsonConstructor]
    public CompileDebugReportActivity() { }

    public CompileDebugReportActivity(ILogger<CompileDebugReportActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context) ?? "unknown";
        var mode = DebugContextMode.Get(context) ?? "unknown";
        var hypothesesJson = HypothesesJson.Get(context) ?? "[]";
        var attemptsJson = AttemptsJson.Get(context) ?? "[]";
        var remainingFailuresStr = RemainingFailures.Get(context) ?? "";
        var filesStr = FilesInvestigated.Get(context) ?? "";
        var startTimeStr = StartTime.Get(context) ?? DateTime.UtcNow.ToString("o");

        _logger?.LogInformation(
            "Compiling debug report for session {SessionId}, story {StoryId}",
            sessionId, storyId);

        try
        {
            var hypotheses = DeserializeList<Hypothesis>(hypothesesJson);
            var attempts = DeserializeList<FixAttempt>(attemptsJson);
            var failures = DeserializeStringList(remainingFailuresStr);
            var files = DeserializeStringList(filesStr);

            var startTime = DateTime.TryParse(startTimeStr, out var parsed)
                ? parsed : DateTime.UtcNow;
            var totalTime = DateTime.UtcNow - startTime;

            // Generate suggested next steps based on what was tried
            var suggestedSteps = GenerateSuggestedNextSteps(hypotheses, attempts, mode);

            var report = new DebugReport
            {
                AllHypotheses = hypotheses,
                AllAttempts = attempts,
                RemainingFailures = failures,
                FilesInvestigated = files,
                SuggestedNextSteps = suggestedSteps,
                TotalDebugTime = totalTime,
                ReportText = FormatReportText(storyId, mode, hypotheses, attempts, failures, files, suggestedSteps, totalTime)
            };

            _logger?.LogInformation(
                "Debug report compiled: {HypCount} hypotheses, {AttemptCount} attempts, {FailureCount} remaining failures",
                report.AllHypotheses.Count, report.AllAttempts.Count, report.RemainingFailures.Count);

            context.SetResult(report);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to compile debug report");
            context.SetResult(new DebugReport
            {
                ReportText = $"Report compilation failed: {ex.Message}",
                SuggestedNextSteps = new List<string> { "Manual investigation required — report compilation failed" }
            });
        }

        await ValueTask.CompletedTask;
    }

    private static List<T> DeserializeList<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();
        }
        catch
        {
            return new List<T>();
        }
    }

    private static List<string> DeserializeStringList(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new List<string>();

        try
        {
            // Try JSON array first
            return JsonSerializer.Deserialize<List<string>>(input) ?? new List<string>();
        }
        catch
        {
            // Fall back to newline-separated
            return input.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }
    }

    private static List<string> GenerateSuggestedNextSteps(
        List<Hypothesis> hypotheses, List<FixAttempt> attempts, string mode)
    {
        var steps = new List<string>();

        // Analyze what was tried
        var triedApproaches = attempts.Select(a => a.Approach).ToHashSet();

        // Check for patterns in failures
        var madeWorse = hypotheses.Where(h => h.Outcome == HypothesisOutcome.MadeWorse).ToList();
        if (madeWorse.Count > 0)
        {
            steps.Add($"WARNING: {madeWorse.Count} fix attempt(s) made the situation worse — " +
                "consider reverting those changes before proceeding");
        }

        // Untried hypotheses
        var untried = hypotheses.Where(h => h.Outcome == HypothesisOutcome.Untried).ToList();
        if (untried.Count > 0)
        {
            steps.Add($"There are {untried.Count} untried hypotheses remaining — " +
                "consider investigating: " +
                string.Join("; ", untried.Select(h => h.Description)));
        }

        // Mode-specific suggestions
        switch (mode)
        {
            case "TddFailure":
                steps.Add("Consider simplifying the implementation to pass one test at a time");
                steps.Add("Review the test expectations — they may be testing the wrong behavior");
                break;
            case "RuntimeError":
                steps.Add("Add more logging/tracing around the failure point to narrow down the issue");
                steps.Add("Check for environment-specific factors (config, permissions, dependencies)");
                break;
            case "BugInvestigation":
                steps.Add("Verify the bug reproduction steps are correct and deterministic");
                steps.Add("Check if the bug only occurs under specific conditions (data, timing, load)");
                break;
        }

        steps.Add("Consider pair-programming with a senior developer on this issue");
        steps.Add("Check if similar bugs have been reported/fixed in the project history");

        return steps;
    }

    private static string FormatReportText(
        string storyId, string mode,
        List<Hypothesis> hypotheses, List<FixAttempt> attempts,
        List<string> failures, List<string> files,
        List<string> suggestedSteps, TimeSpan totalTime)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# Debug Report — Story: {storyId}");
        sb.AppendLine($"**Mode:** {mode}");
        sb.AppendLine($"**Total Debug Time:** {totalTime.TotalMinutes:F1} minutes");
        sb.AppendLine($"**Attempts:** {attempts.Count}");
        sb.AppendLine($"**Status:** ESCALATED (max iterations reached)");
        sb.AppendLine();

        sb.AppendLine("## Hypotheses");
        foreach (var h in hypotheses.OrderBy(h => h.Rank))
        {
            var outcomeIcon = h.Outcome switch
            {
                HypothesisOutcome.FixedIssue => "[FIXED]",
                HypothesisOutcome.DidNotFix => "[FAILED]",
                HypothesisOutcome.MadeWorse => "[WORSE]",
                _ => "[UNTRIED]"
            };
            sb.AppendLine($"  {h.Rank}. {outcomeIcon} {h.Description} (confidence: {h.Confidence:F2})");
            if (!string.IsNullOrEmpty(h.FailureReason))
                sb.AppendLine($"     Failure reason: {h.FailureReason}");
        }
        sb.AppendLine();

        sb.AppendLine("## Fix Attempts");
        foreach (var a in attempts)
        {
            sb.AppendLine($"  Iteration {a.Iteration}: {a.Approach}");
            sb.AppendLine($"    Result: {a.TestResult}");
            sb.AppendLine($"    Duration: {a.Duration.TotalSeconds:F1}s");
        }
        sb.AppendLine();

        if (failures.Count > 0)
        {
            sb.AppendLine("## Remaining Failures");
            foreach (var f in failures)
                sb.AppendLine($"  - {f}");
            sb.AppendLine();
        }

        if (files.Count > 0)
        {
            sb.AppendLine("## Files Investigated");
            foreach (var f in files)
                sb.AppendLine($"  - {f}");
            sb.AppendLine();
        }

        sb.AppendLine("## Suggested Next Steps");
        for (var i = 0; i < suggestedSteps.Count; i++)
            sb.AppendLine($"  {i + 1}. {suggestedSteps[i]}");

        return sb.ToString();
    }
}
