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
/// Re-requests a code review after the junior has pushed fixes.
/// Checks if the maximum iteration count has been reached; if so,
/// signals that escalation is needed. Otherwise, notifies the reviewer
/// that fixes are ready for re-review.
/// </summary>
[Activity(
    "Tamma.Review",
    "Re-Request Review",
    "Re-request a code review after fixes have been pushed",
    Kind = ActivityKind.Task
)]
public class ReRequestReviewActivity : CodeActivity<ReRequestReviewOutput>
{
    private readonly ILogger<ReRequestReviewActivity>? _logger;
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

    /// <summary>Current fix iteration (1-based)</summary>
    [Input(Description = "Current fix iteration")]
    public Input<int> Iteration { get; set; } = default!;

    /// <summary>Maximum allowed fix iterations (default: 5)</summary>
    [Input(Description = "Maximum fix iterations", DefaultValue = 5)]
    public Input<int> MaxIterations { get; set; } = new(5);

    [JsonConstructor]
    public ReRequestReviewActivity() { }

    public ReRequestReviewActivity(
        ILogger<ReRequestReviewActivity> logger,
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
        var iteration = Iteration.Get(context);
        var maxIterations = MaxIterations.Get(context);

        _logger?.LogInformation(
            "Re-requesting review for PR #{PRNumber}, iteration {Iteration}/{MaxIterations}",
            prNumber, iteration, maxIterations);

        try
        {
            // Check if we have exceeded the maximum iterations
            if (iteration >= maxIterations)
            {
                _logger?.LogWarning(
                    "Max iterations ({MaxIterations}) reached for PR #{PRNumber}, session {SessionId}",
                    maxIterations, prNumber, sessionId);

                context.SetResult(new ReRequestReviewOutput
                {
                    Success = false,
                    PRNumber = prNumber,
                    Iteration = iteration,
                    MaxIterationsReached = true,
                    Message = $"Maximum fix iterations ({maxIterations}) reached. Escalation needed."
                });
                return;
            }

            var junior = await _repository!.GetJuniorByIdAsync(juniorId);

            // Notify the junior that re-review is in progress — Story 38-3b: enqueue
            // the DM intent via the API seam (engine holds no Slack credential);
            // fire-and-forget, fail-soft.
            if (junior != null && !string.IsNullOrEmpty(junior.SlackId))
            {
                await MediatedSlack.QueueDirectMessageAsync(
                    context,
                    junior.SlackId,
                    $"Your fixes for PR #{prNumber} have been submitted (iteration {iteration}). " +
                    "The PR is being re-reviewed. Hang tight!",
                    "Info", "SendDirect", context.CancellationToken);
            }

            // Log the event
            await _repository.LogEventAsync(new MentorshipEvent
            {
                SessionId = sessionId,
                EventType = EventTypes.CodeReviewUpdate,
                StateFrom = Tamma.Core.Enums.MentorshipState.GUIDE_FIXES,
                StateTo = Tamma.Core.Enums.MentorshipState.RE_REQUEST_REVIEW
            });

            _logger?.LogInformation(
                "Re-review requested for PR #{PRNumber}, iteration {Iteration}",
                prNumber, iteration);

            context.SetResult(new ReRequestReviewOutput
            {
                Success = true,
                PRNumber = prNumber,
                Iteration = iteration,
                MaxIterationsReached = false,
                Message = $"Re-review requested for PR #{prNumber} (iteration {iteration})"
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error re-requesting review for PR #{PRNumber}", prNumber);
            context.SetResult(new ReRequestReviewOutput
            {
                Success = false,
                PRNumber = prNumber,
                Iteration = iteration,
                Message = $"Failed to re-request review: {ex.Message}"
            });
        }
    }
}

/// <summary>
/// Output from re-requesting a review
/// </summary>
public class ReRequestReviewOutput
{
    public bool Success { get; set; }
    public int PRNumber { get; set; }
    public int Iteration { get; set; }
    public bool MaxIterationsReached { get; set; }
    public string? Message { get; set; }
}
