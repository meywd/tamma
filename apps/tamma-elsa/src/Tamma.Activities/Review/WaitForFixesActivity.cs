using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Review.Models;

namespace Tamma.Activities.Review;

/// <summary>
/// Bookmark-based blocking activity that suspends the workflow until the junior
/// pushes fixes. Creates a bookmark named "fixes-{sessionId}-{prNumber}-{iteration}"
/// and waits for an external signal carrying a <see cref="FixesSubmittedPayload"/>.
///
/// Outcomes:
///   - FixesReceived: junior pushed fixes
///   - TimedOut: no fixes within the timeout window
///
/// <para><b>Durable hour-granular fix-submission wait (review fix 2026-06-25, code-review P0).</b>
/// Two resume paths are armed when the activity suspends: the fixes bookmark
/// (<c>fixes-{session}-{pr}-{iteration}</c>) resumed by the junior's push →
/// <see cref="FixesReceived"/>; and a DURABLE delay bookmark via
/// <see cref="Elsa.Extensions.DelayActivityExecutionContextExtensions.DelayFor(ActivityExecutionContext, System.TimeSpan, ExecuteActivityDelegate)"/>
/// at the timeout → <see cref="TimedOut"/>. The build-out only stored an in-memory deadline and
/// checked it INSIDE the resume callback — but if the junior never pushes, the callback never
/// fires, so the <c>TimedOut</c> outcome was runtime-unreachable and the instance suspended
/// forever. The Delay bookmark is EF-persisted and re-armed by <c>Elsa.Scheduling</c>'s startup
/// task on rehydration, so an unanswered fix request now terminates as a real <c>TimedOut</c>
/// even across a host restart. Whichever path resumes first completes the activity; Elsa burns
/// the remaining bookmark. The wait is hour-granular: <see cref="TimeoutHours"/> is supplied by
/// the workflow's resolved <c>CodeReview:FixTimeoutMinutes</c> config, floored at 1h.</para>
/// </summary>
[Activity(
    "Tamma.Review",
    "Wait For Fixes",
    "Wait for the junior developer to push fixes (bookmark-based)",
    Kind = ActivityKind.Task
)]
[FlowNode("FixesReceived", "TimedOut")]
public class WaitForFixesActivity : Activity
{
    private readonly ILogger<WaitForFixesActivity>? _logger;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<string> SessionId { get; set; } = default!;

    /// <summary>Pull request number</summary>
    [Input(Description = "Pull request number")]
    public Input<int> PRNumber { get; set; } = default!;

    /// <summary>Current fix iteration (1-based)</summary>
    [Input(Description = "Current fix iteration")]
    public Input<int> Iteration { get; set; } = default!;

    /// <summary>Timeout in hours for the junior to push fixes (default: 24)</summary>
    [Input(Description = "Fix timeout in hours", DefaultValue = 24)]
    public Input<int> TimeoutHours { get; set; } = new(24);

    /// <summary>The fixes payload received from the external signal</summary>
    [Output(Description = "Fixes submitted payload")]
    public Output<FixesSubmittedPayload?> FixesPayload { get; set; } = default!;

    [JsonConstructor]
    public WaitForFixesActivity() { }

    public WaitForFixesActivity(ILogger<WaitForFixesActivity> logger)
    {
        _logger = logger;
    }

    protected override void Execute(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var prNumber = PRNumber.Get(context);
        var iteration = Iteration.Get(context);
        var timeoutHours = TimeoutHours.Get(context);

        var bookmarkName = $"fixes-{sessionId}-{prNumber}-{iteration}";

        _logger?.LogInformation(
            "Creating fixes bookmark {BookmarkName}, timeout {TimeoutHours}h",
            bookmarkName, timeoutHours);

        // Store deadline for the belt-and-braces guard on resume.
        context.SetVariable("FixDeadline", DateTime.UtcNow.AddHours(timeoutHours));

        // 1) Fixes bookmark — resumed by the junior's push. The workflow suspends here until
        //    this resumes OR the durable timeout delay below fires.
        context.CreateBookmark(
            new CreateBookmarkArgs
            {
                BookmarkName = bookmarkName,
                Callback = OnFixesReceivedAsync,
                AutoBurn = true
            });

        // 2) Durable fix-submission timeout — a DelayFor (Delay) bookmark that Elsa.Scheduling's
        //    startup task RE-ARMS after a host restart (EF-persisted, not an in-memory timer).
        //    A never-answered fix request terminates as a real TimedOut even across a restart.
        //    A non-positive timeout disables the deadline (wait indefinitely).
        if (timeoutHours > 0)
        {
            context.DelayFor(TimeSpan.FromHours(timeoutHours), OnTimeoutAsync);
        }
    }

    /// <summary>
    /// Durable timeout path: the fix-submission window elapsed with no push. The Delay bookmark
    /// scheduler resumes the activity here (and re-arms across a host restart). Takes the
    /// <c>TimedOut</c> outcome instead of suspending forever; the still-armed fixes bookmark is
    /// burned on completion.
    /// </summary>
    private async ValueTask OnTimeoutAsync(ActivityExecutionContext context)
    {
        var prNumber = PRNumber.Get(context);
        var iteration = Iteration.Get(context);

        _logger?.LogWarning(
            "Fix-submission window expired (durable timeout) for PR #{PRNumber}, iteration {Iteration} — taking the TimedOut outcome",
            prNumber, iteration);

        FixesPayload.Set(context, (FixesSubmittedPayload?)null);
        await context.CompleteActivityWithOutcomesAsync("TimedOut");
    }

    private async ValueTask OnFixesReceivedAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var prNumber = PRNumber.Get(context);
        var iteration = Iteration.Get(context);

        // Check for timeout
        var deadline = context.GetVariable<DateTime>("FixDeadline");
        if (deadline != default && DateTime.UtcNow > deadline)
        {
            _logger?.LogWarning(
                "Fix submission timed out for PR #{PRNumber}, iteration {Iteration}",
                prNumber, iteration);

            FixesPayload.Set(context, (FixesSubmittedPayload?)null);
            await context.CompleteActivityWithOutcomesAsync("TimedOut");
            return;
        }

        // Extract the fixes payload from workflow input
        var input = context.WorkflowInput;

        var commitSha = input.TryGetValue("CommitSha", out var sha) ? sha?.ToString() : null;
        var message = input.TryGetValue("Message", out var msg) ? msg?.ToString() : null;

        var filesChanged = new List<string>();
        if (input.TryGetValue("FilesChanged", out var files) && files is IEnumerable<object> fileList)
        {
            filesChanged.AddRange(fileList.Select(f => f?.ToString() ?? ""));
        }

        var payload = new FixesSubmittedPayload
        {
            SessionId = sessionId,
            PRNumber = prNumber,
            Iteration = iteration,
            CommitSha = commitSha,
            FilesChanged = filesChanged,
            Message = message
        };

        FixesPayload.Set(context, payload);

        _logger?.LogInformation(
            "Fixes received for PR #{PRNumber}, iteration {Iteration}: {FileCount} files changed",
            prNumber, iteration, filesChanged.Count);

        await context.CompleteActivityWithOutcomesAsync("FixesReceived");
    }
}
