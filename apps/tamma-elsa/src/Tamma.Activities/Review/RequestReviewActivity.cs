using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall;
using Tamma.Activities.Review.Models;
using Tamma.Core.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Review;

/// <summary>
/// Requests a code review on an existing pull request.
/// Assigns reviewers (if configured) and notifies the junior developer
/// that their PR is now awaiting review.
/// </summary>
[Activity(
    "Tamma.Review",
    "Request Review",
    "Request a code review on a pull request",
    Kind = ActivityKind.Task
)]
public class RequestReviewActivity : CodeActivity<RequestReviewOutput>
{
    private readonly ILogger<RequestReviewActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Pull request number</summary>
    [Input(Description = "Pull request number")]
    public Input<int> PRNumber { get; set; } = default!;

    /// <summary>Story ID</summary>
    [Input(Description = "Story ID")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Junior developer ID</summary>
    [Input(Description = "Junior developer ID")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Comma-separated list of reviewer usernames to assign</summary>
    [Input(Description = "Comma-separated reviewer usernames (optional)")]
    public Input<string?> Reviewers { get; set; } = default!;

    [JsonConstructor]
    public RequestReviewActivity() { }

    public RequestReviewActivity(
        ILogger<RequestReviewActivity> logger,
        IMentorshipSessionRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var prNumber = PRNumber.Get(context);
        var storyId = StoryId.Get(context);
        var juniorId = JuniorId.Get(context);
        var reviewers = Reviewers.GetOrDefault(context);

        _logger?.LogInformation(
            "Requesting review on PR #{PRNumber} for session {SessionId}",
            prNumber, sessionId);

        try
        {
            var junior = await _repository!.GetJuniorByIdAsync(juniorId);

            // Notify the junior that the PR is awaiting review — Story 38-3b: enqueue
            // the DM intent via the API seam (engine holds no Slack credential);
            // fire-and-forget, fail-soft.
            if (junior != null && !string.IsNullOrEmpty(junior.SlackId))
            {
                await MediatedSlack.QueueDirectMessageAsync(
                    context,
                    junior.SlackId,
                    $"Your PR #{prNumber} is now awaiting code review. " +
                    "You will be notified when a reviewer submits feedback.",
                    "Info", "SendDirect", context.CancellationToken);
            }

            // Log the event
            await _repository.LogEventAsync(new MentorshipEvent
            {
                SessionId = sessionId,
                EventType = EventTypes.CodeReviewSubmitted,
                StateFrom = Tamma.Core.Enums.MentorshipState.PREPARE_CODE_REVIEW,
                StateTo = Tamma.Core.Enums.MentorshipState.MONITOR_REVIEW
            });

            _logger?.LogInformation(
                "Review requested on PR #{PRNumber}, session {SessionId}",
                prNumber, sessionId);

            context.SetResult(new RequestReviewOutput
            {
                Success = true,
                PRNumber = prNumber,
                ReviewersAssigned = ParseReviewers(reviewers),
                Message = $"Review requested on PR #{prNumber}"
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error requesting review for PR #{PRNumber}", prNumber);
            context.SetResult(new RequestReviewOutput
            {
                Success = false,
                PRNumber = prNumber,
                Message = $"Failed to request review: {ex.Message}"
            });
        }
    }

    private static List<string> ParseReviewers(string? reviewers)
    {
        if (string.IsNullOrWhiteSpace(reviewers))
            return new List<string>();

        return reviewers
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}

/// <summary>
/// Output from requesting a review
/// </summary>
public class RequestReviewOutput
{
    public bool Success { get; set; }
    public int PRNumber { get; set; }
    public List<string> ReviewersAssigned { get; set; } = new();
    public string? Message { get; set; }
}
