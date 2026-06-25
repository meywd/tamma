using Elsa.Extensions;
using Elsa.Scheduling;
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
/// <para><b>Durable wait-timeout (completeness audit 2026-06-22, 7-1G AC6 / §Missing #1+#2).</b>
/// Alongside the progress bookmark this activity schedules a delayed resume of the SAME
/// bookmark at <c>now + WaitTimeMinutes</c> via <see cref="IWorkflowScheduler"/> (the durable
/// scheduling seam <c>UseScheduling()</c> wires). If no external progress signal arrives first,
/// the scheduled resume fires with <c>Timeout=true</c> → <see cref="TimedOut"/> output and
/// <see cref="ProgressDetected"/>=false, and the activity completes. This closes the
/// hang-forever hole: a never-resumed bookmark now always terminates as a real per-level
/// timeout rather than suspending indefinitely. The wait time is sourced from config
/// (<c>BlockerDiagnosis:WaitTimeMinutes:{level}</c>, optionally <c>:Extended</c> for skill ≥
/// the configured extended-skill floor) with the historical constants as the fallback.</para>
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

    protected override void Execute(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var currentLevel = CurrentLevel.Get(context);
        var waitTimeMinutes = ResolveWaitMinutes(context, currentLevel);

        _logger?.LogInformation(
            "Waiting for progress detection: Session={SessionId}, Level={Level}, Timeout={Timeout}min",
            sessionId, currentLevel, waitTimeMinutes);

        // Create the progress bookmark with a deterministic id so the scheduled timeout can
        // resume THIS bookmark. The workflow suspends here until external resume OR the
        // scheduled timeout below fires.
        var bookmarkId = Guid.NewGuid().ToString("N");
        context.CreateBookmark(new CreateBookmarkArgs
        {
            BookmarkId = bookmarkId,
            BookmarkName = $"blocker-progress-{sessionId}-{currentLevel}",
            Callback = OnResumeAsync,
            AutoBurn = true
        });

        ScheduleTimeout(context, bookmarkId, waitTimeMinutes);
    }

    /// <summary>
    /// Schedule a durable resume of the progress bookmark at <c>now + waitMinutes</c> carrying
    /// <c>Timeout=true</c>. Best-effort: if the scheduling seam is unavailable the bookmark
    /// still exists (so an external progress signal can resume it) — we log loudly rather
    /// than fail the run, but the no-hang guarantee only holds when scheduling is wired
    /// (<c>UseScheduling()</c>, which the engine host enables).
    /// </summary>
    private void ScheduleTimeout(ActivityExecutionContext context, string bookmarkId, int waitMinutes)
    {
        var scheduler = context.GetService<IWorkflowScheduler>();
        if (scheduler is null)
        {
            _logger?.LogWarning(
                "IWorkflowScheduler unavailable — progress bookmark {BookmarkId} has no durable timeout "
                + "(relies on an external resume).", bookmarkId);
            return;
        }

        var instanceId = context.WorkflowExecutionContext.Id;
        var resumeAt = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, waitMinutes));
        var taskName = $"blocker-progress-timeout-{instanceId}-{bookmarkId}";

        var request = new ScheduleExistingWorkflowInstanceRequest
        {
            WorkflowInstanceId = instanceId,
            BookmarkId = bookmarkId,
            Input = new Dictionary<string, object> { ["Timeout"] = true }
        };

        // Fire-and-forget the async schedule call from this sync Execute; ScheduleAtAsync only
        // enqueues a scheduler job (no workflow execution happens inline).
        scheduler.ScheduleAtAsync(taskName, request, resumeAt).AsTask().GetAwaiter().GetResult();

        _logger?.LogInformation(
            "Scheduled progress-timeout for bookmark {BookmarkId} at {ResumeAt:o} ({WaitMinutes}min)",
            bookmarkId, resumeAt, waitMinutes);
    }

    private async ValueTask OnResumeAsync(ActivityExecutionContext context)
    {
        var input = context.WorkflowInput;

        var isTimeout = input.TryGetValue("Timeout", out var to) && to is true;
        var progressDetected = !isTimeout
            && input.TryGetValue("ProgressDetected", out var pd)
            && pd is true;
        var progressType = isTimeout
            ? "Timeout"
            : input.TryGetValue("ProgressType", out var pt) ? pt?.ToString() : null;
        var details = isTimeout
            ? "Per-level wait expired with no progress"
            : input.TryGetValue("Details", out var d) ? d?.ToString() : null;

        var result = new ProgressDetectionResult
        {
            ProgressDetected = progressDetected,
            ProgressType = progressType,
            Details = details
        };

        _logger?.LogInformation(
            "Progress detection resumed: Detected={Detected}, TimedOut={TimedOut}, Type={Type}",
            progressDetected, isTimeout, progressType);

        context.Set(ProgressDetected, progressDetected);
        context.Set(TimedOut, isTimeout);
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
