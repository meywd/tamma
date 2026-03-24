using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Review.Models;
using Tamma.Core.Entities;
using Tamma.Core.Enums;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Review;

/// <summary>
/// Merges the approved pull request and completes the review sub-workflow.
/// Supports configurable merge strategies (squash, merge, rebase) with
/// squash as the default. Notifies the junior developer upon merge.
/// </summary>
[Activity(
    "Tamma.Review",
    "Merge And Complete Review",
    "Merge the approved PR and complete the code review sub-workflow",
    Kind = ActivityKind.Task
)]
public class MergeAndCompleteReviewActivity : CodeActivity<ReviewMergeResult>
{
    private readonly ILogger<MergeAndCompleteReviewActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IIntegrationService? _integrationService;
    private readonly IAnalyticsService? _analyticsService;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story ID</summary>
    [Input(Description = "Story ID")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Junior developer ID</summary>
    [Input(Description = "Junior developer ID")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Pull request number to merge</summary>
    [Input(Description = "Pull request number")]
    public Input<int> PRNumber { get; set; } = default!;

    /// <summary>Merge strategy (default: Squash)</summary>
    [Input(Description = "Merge strategy: Squash, Merge, Rebase", DefaultValue = MergeStrategy.Squash)]
    public Input<MergeStrategy> Strategy { get; set; } = new(MergeStrategy.Squash);

    /// <summary>Total fix iterations that occurred during review</summary>
    [Input(Description = "Total fix iterations during review", DefaultValue = 0)]
    public Input<int> TotalIterations { get; set; } = new(0);

    [JsonConstructor]
    public MergeAndCompleteReviewActivity() { }

    public MergeAndCompleteReviewActivity(
        ILogger<MergeAndCompleteReviewActivity> logger,
        IMentorshipSessionRepository repository,
        IIntegrationService integrationService,
        IAnalyticsService analyticsService)
    {
        _logger = logger;
        _repository = repository;
        _integrationService = integrationService;
        _analyticsService = analyticsService;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var juniorId = JuniorId.Get(context);
        var prNumber = PRNumber.Get(context);
        var strategy = Strategy.Get(context);
        var totalIterations = TotalIterations.Get(context);

        _logger?.LogInformation(
            "Merging PR #{PRNumber} with strategy {Strategy} for session {SessionId}",
            prNumber, strategy, sessionId);

        try
        {
            var story = await _repository!.GetStoryByIdAsync(storyId);
            var junior = await _repository.GetJuniorByIdAsync(juniorId);

            if (story == null || string.IsNullOrEmpty(story.RepositoryUrl))
            {
                context.SetResult(new ReviewMergeResult
                {
                    Success = false,
                    StrategyUsed = strategy,
                    Error = "Story or repository URL not found"
                });
                return;
            }

            // Merge the PR (the integration service handles the strategy internally)
            var mergeResult = await _integrationService!.MergeGitHubPullRequestAsync(
                story.RepositoryUrl, prNumber);

            if (!mergeResult.Success)
            {
                _logger?.LogWarning(
                    "Merge failed for PR #{PRNumber}: {Error}", prNumber, mergeResult.Error);

                context.SetResult(new ReviewMergeResult
                {
                    Success = false,
                    StrategyUsed = strategy,
                    Error = $"Merge failed: {mergeResult.Error}"
                });
                return;
            }

            // Log the event
            await _repository.LogEventAsync(new MentorshipEvent
            {
                SessionId = sessionId,
                EventType = EventTypes.CodeReviewApproved,
                StateFrom = MentorshipState.MONITOR_REVIEW,
                StateTo = MentorshipState.MERGE_AND_COMPLETE
            });

            // Record analytics
            await _analyticsService!.RecordMetricAsync(sessionId, "pr_merged", 1);
            await _analyticsService.RecordMetricAsync(sessionId, "review_iterations", totalIterations);

            // Notify junior
            if (junior != null && !string.IsNullOrEmpty(junior.SlackId))
            {
                var iterationNote = totalIterations > 0
                    ? $" after {totalIterations} fix iteration(s)"
                    : "";

                await _integrationService.SendSlackDirectMessageAsync(
                    junior.SlackId,
                    $"Your PR #{prNumber} has been approved and merged{iterationNote}! " +
                    "Great work on completing the code review process.");
            }

            _logger?.LogInformation(
                "PR #{PRNumber} merged successfully for session {SessionId}, sha {MergeSha}",
                prNumber, sessionId, mergeResult.MergeSha);

            context.SetResult(new ReviewMergeResult
            {
                Success = true,
                MergeSha = mergeResult.MergeSha,
                StrategyUsed = strategy
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error merging PR #{PRNumber} for session {SessionId}", prNumber, sessionId);
            context.SetResult(new ReviewMergeResult
            {
                Success = false,
                StrategyUsed = strategy,
                Error = $"Merge failed: {ex.Message}"
            });
        }
    }
}
