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

        // Store the deadline so we can check on resume
        context.SetVariable("ReviewDeadline", DateTime.UtcNow.AddHours(timeoutHours));

        // Create the bookmark — workflow suspends here
        context.CreateBookmark(
            new CreateBookmarkArgs
            {
                BookmarkName = bookmarkName,
                Callback = OnReviewReceivedAsync,
                AutoBurn = true
            });
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
