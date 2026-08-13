using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Testing.Models;

namespace Tamma.Activities.Testing;

/// <summary>
/// Bookmark-based ELSA activity that suspends the workflow until CI results arrive — OR
/// the configured <see cref="TimeoutMinutes"/> deadline elapses, whichever happens first.
///
/// <para>Build-out (completeness audit 2026-06-22, <c>Testing.md</c> §Missing #2): the
/// previously-dead <see cref="TimeoutMinutes"/> input is now ENFORCED. Two resume paths
/// are armed when the activity suspends:</para>
/// <list type="number">
///   <item><description>a CI-result bookmark (<c>ci-result-{sessionId}-{runId}</c>)
///     resumed by the external CI webhook → <c>Received</c> outcome; and</description></item>
///   <item><description>a durable scheduled delay bookmark (via
///     <c>DelayActivityExecutionContextExtensions.DelayFor</c>, the same primitive the
///     framework's <c>Delay</c> activity uses) that the bookmark scheduler auto-resumes at
///     the deadline → <c>Timeout</c> outcome.</description></item>
/// </list>
/// <para>Whichever resumes first completes the activity; Elsa burns the activity's
/// remaining bookmark on completion, so the loser is discarded. On timeout the activity
/// emits a sentinel <see cref="CIResultsPayload"/> (<c>Status="TimedOut"</c>,
/// <c>BuildPassed=false</c>) and takes the deterministic <c>Timeout</c> edge — the workflow
/// can never suspend forever (no permanent-hang / silent false success). The delay is
/// durable (scheduler-driven), not a blocking <c>Task.Delay</c>, so no thread is held for
/// the (default 30-minute) wait.</para>
/// </summary>
[Activity(
    "Tamma.Testing",
    "Wait For CI Results",
    "Suspend workflow execution until CI pipeline results are received via bookmark, or time out",
    Kind = ActivityKind.Task
)]
[FlowNode("Received", "Timeout")]
public class WaitForCIResultsActivity : Activity
{
    /// <summary>
    /// Epic 31 P3 (DG-5) — the COMMON stimulus name every CI-result wait
    /// bookmark is registered under, so the CI completion poller can discover
    /// all suspended CI waits with one <c>BookmarkFilter.Name</c> query
    /// (individual waits are then identified by their payload + bookmark id).
    /// </summary>
    public const string CiWaitStimulusName = "ci-result-wait";

    private readonly ILogger<WaitForCIResultsActivity>? _logger;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>CI run ID returned by TriggerCIActivity</summary>
    [Input(Description = "CI pipeline run ID")]
    public Input<string> RunId { get; set; } = default!;

    /// <summary>Repository (owner/repo) the run belongs to — carried on the
    /// bookmark payload so the completion poller can poll the run (DG-5).</summary>
    [Input(Description = "Repository (owner/repo) the CI run belongs to")]
    public Input<string?> Repository { get; set; } = default!;

    /// <summary>Timeout in minutes for waiting</summary>
    [Input(Description = "Timeout in minutes", DefaultValue = 30)]
    public Input<int> TimeoutMinutes { get; set; } = new(30);

    /// <summary>The CI results received when resumed (or a TimedOut sentinel on timeout)</summary>
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
        var timeoutMinutes = TimeoutMinutes.Get(context);
        var repository = Repository.GetOrDefault(context);
        var tenantId = ResolveTenantId(context);

        var bookmarkPayload = new CIResultBookmarkPayload(sessionId, runId, repository, tenantId);

        _logger?.LogInformation(
            "Creating bookmark for CI results: {BookmarkId} (session={SessionId}, run={RunId}, repo={Repository}, timeout={TimeoutMinutes}m)",
            bookmarkPayload.BookmarkId, sessionId, runId, repository ?? "<none>", timeoutMinutes);

        // 1) CI-result bookmark — resumed by the CI completion poller (DG-5)
        //    or, in P4, the workflow_run webhook accelerator. Registered under
        //    the COMMON stimulus name so the poller can enumerate CI waits;
        //    the payload identifies the specific run + repo + tenant.
        context.CreateBookmark(new CreateBookmarkArgs
        {
            BookmarkName = CiWaitStimulusName,
            Stimulus = bookmarkPayload,
            Callback = OnResumeAsync,
            AutoBurn = true,
            IncludeActivityInstanceId = false,
        });

        // 2) Durable timeout bookmark — the bookmark scheduler resumes it at the deadline.
        //    A non-positive timeout disables the deadline (wait indefinitely) — the caller
        //    is responsible for supplying a positive timeout (the workflow always does).
        if (timeoutMinutes > 0)
        {
            context.DelayFor(TimeSpan.FromMinutes(timeoutMinutes), OnTimeoutAsync);
        }
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
                    BuildPassed = input.TryGetValue("BuildPassed", out var bp) && ResumeInput.AsBool(bp),
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
            // Fail closed: a CI result we cannot parse is NOT a pass. Surface a build-failed
            // sentinel (CreateDefaultResults => BuildPassed=false, Status="Unknown") so the
            // gate cannot read a green result out of an unparseable payload.
            _logger?.LogWarning(ex, "Failed to parse CI results — failing closed with a build-failed sentinel");
            results = CreateDefaultResults(runId);
        }

        Results.Set(context, results);
        await context.CompleteActivityWithOutcomesAsync("Received");
    }

    /// <summary>
    /// Resumed by the bookmark scheduler when the timeout deadline elapses before CI
    /// reports. Emits a sentinel build-failed payload and takes the deterministic
    /// <c>Timeout</c> edge so the workflow escalates instead of hanging forever.
    /// </summary>
    private async ValueTask OnTimeoutAsync(ActivityExecutionContext context)
    {
        var runId = RunId.Get(context);
        var timeoutMinutes = TimeoutMinutes.Get(context);

        _logger?.LogWarning(
            "CI wait timed out after {TimeoutMinutes}m for run {RunId} — taking the Timeout edge",
            timeoutMinutes, runId);

        var sentinel = new CIResultsPayload
        {
            RunId = runId,
            Status = "TimedOut",
            BuildPassed = false,
            CompletedAt = DateTime.UtcNow
        };

        Results.Set(context, sentinel);
        await context.CompleteActivityWithOutcomesAsync("Timeout");
    }

    /// <summary>Ambient tenant scope — the MediatedLlmText convention (a Guid or
    /// string workflow variable; anything else ⇒ platform scope).</summary>
    private static string? ResolveTenantId(ActivityExecutionContext context)
    {
        var raw = context.GetVariable<object?>("TenantId")
                  ?? context.GetVariable<object?>("AccountId");
        return raw switch
        {
            Guid g when g != Guid.Empty => g.ToString(),
            string s when Guid.TryParse(s, out var p) && p != Guid.Empty => p.ToString(),
            _ => null,
        };
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
