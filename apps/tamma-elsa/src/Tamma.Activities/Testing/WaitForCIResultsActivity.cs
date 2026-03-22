using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Testing.Models;

namespace Tamma.Activities.Testing;

/// <summary>
/// Bookmark-based ELSA activity that suspends the workflow until CI results arrive.
/// Creates a bookmark with the pattern: ci-result-{sessionId}-{runId}
/// An external webhook or API call resumes the workflow by matching this bookmark payload.
/// </summary>
[Activity(
    "Tamma.Testing",
    "Wait For CI Results",
    "Suspend workflow execution until CI pipeline results are received via bookmark",
    Kind = ActivityKind.Task
)]
public class WaitForCIResultsActivity : Activity
{
    private readonly ILogger<WaitForCIResultsActivity>? _logger;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>CI run ID returned by TriggerCIActivity</summary>
    [Input(Description = "CI pipeline run ID")]
    public Input<string> RunId { get; set; } = default!;

    /// <summary>Timeout in minutes for waiting</summary>
    [Input(Description = "Timeout in minutes", DefaultValue = 30)]
    public Input<int> TimeoutMinutes { get; set; } = new(30);

    /// <summary>The CI results received when resumed</summary>
    [Output(Description = "CI pipeline results")]
    public Output<CIResultsPayload> Results { get; set; } = default!;

    [JsonConstructor]
    public WaitForCIResultsActivity()
    {
        _logger = null;
    }

    public WaitForCIResultsActivity(ILogger<WaitForCIResultsActivity> logger)
    {
        _logger = logger;
    }

    protected override void Execute(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var runId = RunId.Get(context);

        var bookmarkPayload = new CIResultBookmarkPayload(sessionId, runId);

        _logger?.LogInformation(
            "Creating bookmark for CI results: {BookmarkId} (session={SessionId}, run={RunId})",
            bookmarkPayload.BookmarkId, sessionId, runId);

        // Create bookmark — the workflow suspends here until resumed externally
        context.CreateBookmark(bookmarkPayload, OnResumeAsync);
    }

    private async ValueTask OnResumeAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var runId = RunId.Get(context);

        _logger?.LogInformation(
            "CI results received for session {SessionId}, run {RunId}",
            sessionId, runId);

        // Extract CI results from the resume input
        var input = context.WorkflowInput;
        CIResultsPayload results;

        try
        {
            // Try to deserialize from the input dictionary
            if (input.TryGetValue("Results", out var resultsObj) && resultsObj != null)
            {
                if (resultsObj is CIResultsPayload typedResults)
                {
                    results = typedResults;
                }
                else
                {
                    var json = JsonSerializer.Serialize(resultsObj);
                    results = JsonSerializer.Deserialize<CIResultsPayload>(json) ?? CreateDefaultResults(runId);
                }
            }
            else
            {
                // Build results from individual input fields
                results = new CIResultsPayload
                {
                    RunId = runId,
                    Status = input.GetValueOrDefault("Status")?.ToString() ?? "Unknown",
                    BuildPassed = input.TryGetValue("BuildPassed", out var bp) && bp is true,
                    TotalTests = input.TryGetValue("TotalTests", out var tt) && tt is int totalTests ? totalTests : 0,
                    PassedTests = input.TryGetValue("PassedTests", out var pt) && pt is int passedTests ? passedTests : 0,
                    FailedTests = input.TryGetValue("FailedTests", out var ft) && ft is int failedTests ? failedTests : 0,
                    CoveragePercentage = input.TryGetValue("CoveragePercentage", out var cp) && cp is double coverage ? coverage : 0,
                    LintWarnings = input.TryGetValue("LintWarnings", out var lw) && lw is int lintWarnings ? lintWarnings : 0,
                    LintErrors = input.TryGetValue("LintErrors", out var le) && le is int lintErrors ? lintErrors : 0,
                    CompletedAt = DateTime.UtcNow
                };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse CI results, using defaults");
            results = CreateDefaultResults(runId);
        }

        Results.Set(context, results);
        await context.CompleteActivityAsync();
    }

    private static CIResultsPayload CreateDefaultResults(string runId)
    {
        return new CIResultsPayload
        {
            RunId = runId,
            Status = "Unknown",
            BuildPassed = false,
            CompletedAt = DateTime.UtcNow
        };
    }
}
