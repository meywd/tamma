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
/// Bookmark-based blocking activity that suspends the workflow until a review
/// webhook fires. Creates a bookmark named "review-{sessionId}-{prNumber}"
/// and waits for an external signal carrying a <see cref="ReviewWebhookPayload"/>.
///
/// Outcomes:
///   - Approved: reviewer approved the PR
///   - ChangesRequested: reviewer requested changes
///   - TimedOut: no review within the timeout window
///
/// <para><b>Durable review-response timeout (review fix 2026-06-25, code-review P0).</b> Two
/// resume paths are armed when the activity suspends: the review bookmark
/// (<c>review-{session}-{pr}</c>) resumed by the reviewer webhook → <see cref="Approved"/> /
/// <see cref="ChangesRequested"/>; and a DURABLE delay bookmark via
/// <see cref="Elsa.Extensions.DelayActivityExecutionContextExtensions.DelayFor(ActivityExecutionContext, System.TimeSpan, ExecuteActivityDelegate)"/>
/// at the timeout → <see cref="TimedOut"/>. The build-out only stored an in-memory deadline
/// and checked it INSIDE the resume callback — but if the reviewer never responds the callback
/// never fires, so the <c>TimedOut</c> outcome was runtime-unreachable and the instance
/// suspended forever. The Delay bookmark is EF-persisted and re-armed by
/// <c>Elsa.Scheduling</c>'s startup task on rehydration, so a never-answered review now
/// terminates as a real <c>TimedOut</c> even across a host restart. Whichever path resumes
/// first completes the activity; Elsa burns the remaining bookmark (no orphaned timer /
/// double-resume). The in-memory deadline is retained as a belt-and-braces guard inside the
/// real-resume path.</para>
/// </summary>
[Activity(
    "Tamma.Review",
    "Monitor Review",
    "Wait for a code review webhook (bookmark-based)",
    Kind = ActivityKind.Task
)]
[FlowNode("Approved", "ChangesRequested", "TimedOut")]
public class MonitorReviewActivity : Activity
{
    private readonly ILogger<MonitorReviewActivity>? _logger;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<string> SessionId { get; set; } = default!;

    /// <summary>Pull request number to monitor</summary>
    [Input(Description = "Pull request number")]
    public Input<int> PRNumber { get; set; } = default!;

    /// <summary>Timeout in hours before the review is considered timed out (default: 24)</summary>
    [Input(Description = "Review timeout in hours", DefaultValue = 24)]
    public Input<int> TimeoutHours { get; set; } = new(24);

    /// <summary>The review result received from the webhook</summary>
    [Output(Description = "Review result from the webhook")]
    public Output<ReviewResult?> ReviewResult { get; set; } = default!;

    [JsonConstructor]
    public MonitorReviewActivity() { }

    public MonitorReviewActivity(ILogger<MonitorReviewActivity> logger)
    {
        _logger = logger;
    }

    protected override void Execute(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var prNumber = PRNumber.Get(context);
        var timeoutHours = TimeoutHours.Get(context);

        var bookmarkName = $"review-{sessionId}-{prNumber}";

        _logger?.LogInformation(
            "Creating review bookmark {BookmarkName}, timeout {TimeoutHours}h",
            bookmarkName, timeoutHours);

        // Store the deadline so we can guard on resume (belt-and-braces).
        context.SetVariable("ReviewDeadline", DateTime.UtcNow.AddHours(timeoutHours));

        // 1) Review bookmark — resumed by the reviewer webhook. The workflow suspends here
        //    until this resumes OR the durable timeout delay below fires.
        context.CreateBookmark(
            new CreateBookmarkArgs
            {
                BookmarkName = bookmarkName,
                Callback = OnReviewReceivedAsync,
                AutoBurn = true
            });

        // 2) Durable review timeout — a DelayFor (Delay) bookmark that Elsa.Scheduling's
        //    startup task RE-ARMS after a host restart (EF-persisted, not an in-memory timer).
        //    A never-answered review terminates as a real TimedOut even across a restart.
        //    A non-positive timeout disables the deadline (wait indefinitely).
        if (timeoutHours > 0)
        {
            context.DelayFor(TimeSpan.FromHours(timeoutHours), OnTimeoutAsync);
        }
    }

    /// <summary>
    /// Durable timeout path: the review window elapsed with no webhook. The Delay bookmark
    /// scheduler resumes the activity here (and re-arms across a host restart). Takes the
    /// <c>TimedOut</c> outcome instead of suspending forever; the still-armed review bookmark
    /// is burned on completion.
    /// </summary>
    private async ValueTask OnTimeoutAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var prNumber = PRNumber.Get(context);

        _logger?.LogWarning(
            "Review window expired (durable timeout) for PR #{PRNumber}, session {SessionId} — taking the TimedOut outcome",
            prNumber, sessionId);

        ReviewResult.Set(context, new Models.ReviewResult
        {
            Status = PRReviewStatus.TimedOut,
            SubmittedAt = DateTime.UtcNow
        });

        await context.CompleteActivityWithOutcomesAsync("TimedOut");
    }

    private async ValueTask OnReviewReceivedAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var prNumber = PRNumber.Get(context);

        // Check for timeout
        var deadline = context.GetVariable<DateTime>("ReviewDeadline");
        if (deadline != default && DateTime.UtcNow > deadline)
        {
            _logger?.LogWarning(
                "Review timed out for PR #{PRNumber}, session {SessionId}",
                prNumber, sessionId);

            ReviewResult.Set(context, new Models.ReviewResult
            {
                Status = PRReviewStatus.TimedOut
            });

            await context.CompleteActivityWithOutcomesAsync("TimedOut");
            return;
        }

        // Extract the review payload from workflow input
        var input = context.WorkflowInput;

        var statusStr = input.TryGetValue("Status", out var statusVal)
            ? statusVal?.ToString() : null;
        var reviewerLogin = input.TryGetValue("ReviewerLogin", out var rl)
            ? rl?.ToString() : null;
        var reviewBody = input.TryGetValue("ReviewBody", out var rb)
            ? rb?.ToString() : null;

        var status = Enum.TryParse<PRReviewStatus>(statusStr, true, out var parsed)
            ? parsed
            : PRReviewStatus.Pending;

        var result = new Models.ReviewResult
        {
            Status = status,
            ReviewerLogin = reviewerLogin,
            ReviewBody = reviewBody,
            SubmittedAt = DateTime.UtcNow
        };

        // Extract comments if present
        if (input.TryGetValue("Comments", out var commentsVal)
            && commentsVal is IEnumerable<object> commentsList)
        {
            foreach (var c in commentsList)
            {
                if (c is IDictionary<string, object> dict)
                {
                    result.Comments.Add(new ReviewCommentDetail
                    {
                        FilePath = dict.TryGetValue("FilePath", out var fp) ? fp?.ToString() ?? "" : "",
                        Body = dict.TryGetValue("Body", out var body) ? body?.ToString() ?? "" : "",
                        Author = reviewerLogin ?? ""
                    });
                }
            }
        }

        ReviewResult.Set(context, result);

        _logger?.LogInformation(
            "Review received for PR #{PRNumber}: {Status} by {Reviewer}",
            prNumber, status, reviewerLogin);

        var outcome = status switch
        {
            PRReviewStatus.Approved => "Approved",
            PRReviewStatus.ChangesRequested => "ChangesRequested",
            _ => "ChangesRequested"
        };

        await context.CompleteActivityWithOutcomesAsync(outcome);
    }
}
