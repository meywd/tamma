using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.Blocker.Models;

namespace Tamma.Activities.Blocker;

/// <summary>
/// Bookmark-based activity that waits for the junior developer to make progress.
/// Progress can be detected via:
///   - New commits on the branch
///   - CI triggered with new results
///   - Junior sends "resolved" signal via API/Slack
///   - File changes in relevant files
///
/// The workflow suspends at this point and is resumed externally when progress
/// is detected.
///
/// <para><b>Durable wait-timeout (completeness audit 2026-06-22, 7-1G AC6 / §Missing #1+#2;
/// hardened 2026-06-25 to the durable Delay primitive).</b>
/// Two resume paths are armed when the activity suspends:</para>
/// <list type="number">
///   <item><description>a custom progress bookmark (<c>blocker-progress-{session}-{level}</c>)
///     resumed by the external progress signal → <see cref="ProgressDetected"/>; and</description></item>
///   <item><description>a DURABLE delay bookmark via
///     <see cref="Elsa.Extensions.DelayActivityExecutionContextExtensions.DelayFor(ActivityExecutionContext, System.TimeSpan, ExecuteActivityDelegate)"/>
///     — the same EF-persisted Delay primitive the framework's <c>Delay</c> activity uses,
///     which <c>Elsa.Scheduling</c>'s startup background task RE-ARMS after a host restart →
///     <see cref="TimedOut"/>.</description></item>
/// </list>
/// <para>The earlier build used <c>IWorkflowScheduler.ScheduleAtAsync</c>, whose default
/// (in-memory <c>LocalScheduler</c> / <c>System.Timers.Timer</c>) backing is LOST on a host
/// restart → the bookmark would hang forever (the exact P0 this was meant to close). The
/// Delay bookmark is persisted and re-armed on rehydration, so a host restart mid-wait no
/// longer drops the timeout. Whichever path resumes first completes the activity; Elsa burns
/// the activity's remaining bookmark on completion, so there is no orphaned timer / stale
/// double-resume. The wait time is sourced from config
/// (<c>BlockerDiagnosis:WaitTimeMinutes:{level}</c>) with the historical constants as the
/// fallback.</para>
/// </summary>
[Activity(
    "Tamma.Blocker",
    "Detect Progress",
    "Wait for and detect junior's progress after resolution attempt (durable per-level timeout)",
    Kind = ActivityKind.Task
)]
public class DetectProgressActivity : Activity
{
    private readonly ILogger<DetectProgressActivity>? _logger;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story ID</summary>
    [Input(Description = "Story identifier")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Junior developer ID</summary>
    [Input(Description = "Junior developer identifier")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Current resolution level being waited on</summary>
    [Input(Description = "Current resolution level")]
    public Input<string> CurrentLevel { get; set; } = default!;

    /// <summary>Wait time in minutes before timing out (0 → resolve from config/defaults).</summary>
    [Input(Description = "Wait time in minutes before timing out (0 = use BlockerDiagnosis config)", DefaultValue = 0)]
    public Input<int> WaitTimeMinutes { get; set; } = new(0);

    /// <summary>Whether progress was detected</summary>
    [Output(Description = "Whether progress was detected")]
    public Output<bool> ProgressDetected { get; set; } = default!;

    /// <summary>Whether the per-level wait expired with no progress (durable timeout).</summary>
    [Output(Description = "Whether the per-level wait expired with no progress")]
    public Output<bool> TimedOut { get; set; } = default!;

    /// <summary>Details of the progress detection result</summary>
    [Output(Description = "Progress detection result details")]
    public Output<ProgressDetectionResult> Result { get; set; } = default!;

    [JsonConstructor]
    public DetectProgressActivity() { }

    public DetectProgressActivity(ILogger<DetectProgressActivity> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// The SINGLE canonical progress-bookmark name (<c>blocker-progress-{session}-{level}</c>).
    /// Shared by the suspend side (<see cref="Execute"/>) and the resume side
    /// (<c>BlockerResumeEndpoint</c>) so the two match byte-for-byte — the same
    /// suspend/resume-name-parity discipline as
    /// <c>WaitForMergeApprovalActivity.BookmarkName</c>. The name is keyed by the
    /// (globally-unique, unguessable) mentorship session id + resolution level; the
    /// resume endpoint additionally verifies the caller's tenant OWNS that session
    /// before it ever resolves this name (IDOR guard on the Tamma.Api tier).
    /// </summary>
    public static string ProgressBookmarkName(Guid sessionId, string level)
        => $"blocker-progress-{sessionId}-{level}";

    protected override void Execute(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var currentLevel = CurrentLevel.Get(context);
        var waitTimeMinutes = ResolveWaitMinutes(context, currentLevel);

        _logger?.LogInformation(
            "Waiting for progress detection: Session={SessionId}, Level={Level}, Timeout={Timeout}min",
            sessionId, currentLevel, waitTimeMinutes);

        // 1) Progress bookmark — resumed by the external progress signal (commit/CI/junior
        //    "resolved"). The workflow suspends here until this resumes OR the durable delay
        //    below fires, whichever happens first.
        context.CreateBookmark(new CreateBookmarkArgs
        {
            BookmarkName = ProgressBookmarkName(sessionId, currentLevel),
            Callback = OnResumeAsync,
            AutoBurn = true
        });

        // 2) Durable timeout — a DelayFor (Delay) bookmark that Elsa.Scheduling's startup
        //    background task RE-ARMS after a host restart (EF-persisted, not an in-memory
        //    timer). Closes the hang-forever hole: a never-resumed progress bookmark now
        //    always terminates as a real per-level timeout even across a VPS restart.
        context.DelayFor(TimeSpan.FromMinutes(Math.Max(1, waitTimeMinutes)), OnTimeoutAsync);
    }

    /// <summary>
    /// External resume path: the junior made progress. Reads the progress fields from the
    /// resume input, flags <see cref="ProgressDetected"/>, and completes — Elsa burns the
    /// still-armed Delay bookmark on completion (no orphaned timer).
    /// </summary>
    private async ValueTask OnResumeAsync(ActivityExecutionContext context)
    {
        var input = context.WorkflowInput;

        var progressDetected = input.TryGetValue("ProgressDetected", out var pd) && pd is true;
        var progressType = input.TryGetValue("ProgressType", out var pt) ? pt?.ToString() : null;
        var details = input.TryGetValue("Details", out var d) ? d?.ToString() : null;

        var result = new ProgressDetectionResult
        {
            ProgressDetected = progressDetected,
            ProgressType = progressType,
            Details = details
        };

        _logger?.LogInformation(
            "Progress detection resumed (external): Detected={Detected}, Type={Type}",
            progressDetected, progressType);

        context.Set(ProgressDetected, progressDetected);
        context.Set(TimedOut, false);
        context.Set(Result, result);

        await context.CompleteActivityAsync();
    }

    /// <summary>
    /// Durable timeout path: the per-level wait elapsed with no external progress signal. The
    /// Delay bookmark scheduler resumes the activity here (and re-arms across a host restart).
    /// Flags <see cref="TimedOut"/> (and <see cref="ProgressDetected"/>=false) so the ladder
    /// advances instead of suspending forever; the still-armed progress bookmark is burned on
    /// completion.
    /// </summary>
    private async ValueTask OnTimeoutAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var currentLevel = CurrentLevel.Get(context);

        _logger?.LogInformation(
            "Progress detection timed out (durable): Session={SessionId}, Level={Level} — advancing ladder",
            sessionId, currentLevel);

        var result = new ProgressDetectionResult
        {
            ProgressDetected = false,
            ProgressType = "Timeout",
            Details = "Per-level wait expired with no progress"
        };

        context.Set(ProgressDetected, false);
        context.Set(TimedOut, true);
        context.Set(Result, result);

        await context.CompleteActivityAsync();
    }

    /// <summary>
    /// Resolve the per-level wait minutes. Precedence: an explicit positive
    /// <see cref="WaitTimeMinutes"/> input wins; otherwise read
    /// <c>BlockerDiagnosis:WaitTimeMinutes:{level}</c> from config; otherwise the historical
    /// per-level defaults (Hint 15 / Guidance 30 / Assistance 45). For the Hint level a
    /// skill ≥ <c>BlockerDiagnosis:ExtendedHintSkillFloor</c> (default 4) uses
    /// <c>BlockerDiagnosis:WaitTimeMinutes:Hint:Extended</c> (default 30).
    /// </summary>
    internal int ResolveWaitMinutes(ActivityExecutionContext context, string level)
    {
        var explicitValue = WaitTimeMinutes.Get(context);
        var fromConfig = _configuration?.GetValue<int?>($"BlockerDiagnosis:WaitTimeMinutes:{level}");
        return ResolveWaitMinutes(explicitValue, fromConfig, level);
    }

    /// <summary>
    /// Pure wait-minutes resolution (exposed for unit testing). Precedence:
    /// a positive <paramref name="explicitValue"/> input wins; else a positive
    /// <paramref name="configValue"/> (<c>BlockerDiagnosis:WaitTimeMinutes:{level}</c>);
    /// else the historical per-level default (Hint 15 / Guidance 30 / Assistance 45).
    /// </summary>
    internal static int ResolveWaitMinutes(int explicitValue, int? configValue, string level)
    {
        if (explicitValue > 0)
            return explicitValue;
        if (configValue is > 0)
            return configValue.Value;

        return level switch
        {
            "Hint" => 15,
            "Guidance" => 30,
            "Assistance" => 45,
            _ => 15
        };
    }
}
