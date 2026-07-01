using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall;
using Tamma.Activities.Review.Models;
using Tamma.Core.Entities;
using Tamma.Core.Enums;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Review;

/// <summary>
/// Merges the approved pull request and completes the review sub-workflow (Story 7-1D AC8).
///
/// Hardening (completeness audit 2026-06-22, <c>CodeReview.md</c> §Missing #5):
///   - <b>Verify CI green before merge</b> (config-gated, default true): polls the PR's
///     head-branch build status; a non-success status routes to the <c>Failed</c> outcome
///     (the workflow escalates) instead of merging on red.
///   - <b>Honour the merge strategy</b>: the configured <see cref="MergeStrategy"/> is mapped
///     to GitHub's <c>merge_method</c> via the strategy-aware
///     <see cref="IIntegrationService.MergeGitHubPullRequestAsync(string,int,string)"/>
///     overload (the prior single-arg call let the service pick squash unconditionally).
///   - <b>Retry merge once then escalate</b>: a transient merge failure (e.g. a momentary
///     conflict/CI flake) is retried exactly once; a second failure routes to <c>Failed</c>
///     so the workflow escalates to a senior — never a silent false success.
///   - <b>Delete source branch after merge</b> (config-gated, default true): on a successful
///     merge the head branch is deleted (best-effort — a delete failure does NOT fail the
///     merge, it is logged and the merge still reports success).
///
/// Outcomes: <c>Merged</c> (success — proceed to the structured success terminal) /
/// <c>Failed</c> (CI red, or merge failed twice — the workflow escalates).
/// </summary>
[Activity(
    "Tamma.Review",
    "Merge And Complete Review",
    "Merge the approved PR (CI-gated, strategy-aware, retry-once) and complete the code review sub-workflow",
    Kind = ActivityKind.Task
)]
[FlowNode("Merged", "Failed")]
public class MergeAndCompleteReviewActivity : Activity
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

    /// <summary>Head branch to delete after merge (default: feature/{storyId})</summary>
    [Input(Description = "Head branch to delete after merge")]
    public Input<string?> HeadBranch { get; set; } = new((string?)null);

    /// <summary>Merge strategy (default: Squash)</summary>
    [Input(Description = "Merge strategy: Squash, Merge, Rebase", DefaultValue = MergeStrategy.Squash)]
    public Input<MergeStrategy> Strategy { get; set; } = new(MergeStrategy.Squash);

    /// <summary>Total fix iterations that occurred during review</summary>
    [Input(Description = "Total fix iterations during review", DefaultValue = 0)]
    public Input<int> TotalIterations { get; set; } = new(0);

    /// <summary>Verify CI is green before merging (default: true)</summary>
    [Input(Description = "Verify CI green before merge", DefaultValue = true)]
    public Input<bool> VerifyCIBeforeMerge { get; set; } = new(true);

    /// <summary>Delete the source branch after a successful merge (default: true)</summary>
    [Input(Description = "Delete source branch after merge", DefaultValue = true)]
    public Input<bool> DeleteBranchAfterMerge { get; set; } = new(true);

    /// <summary>The merge result</summary>
    [Output(Description = "Merge result")]
    public Output<ReviewMergeResult?> Result { get; set; } = default!;

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
        var verifyCi = VerifyCIBeforeMerge.Get(context);
        var deleteBranch = DeleteBranchAfterMerge.Get(context);

        _logger?.LogInformation(
            "Merging PR #{PRNumber} with strategy {Strategy} for session {SessionId} (verifyCi={VerifyCi})",
            prNumber, strategy, sessionId, verifyCi);

        try
        {
            var story = await _repository!.GetStoryByIdAsync(storyId);
            var junior = await _repository.GetJuniorByIdAsync(juniorId);

            if (story == null || string.IsNullOrEmpty(story.RepositoryUrl))
            {
                await FailAsync(context, strategy, "Story or repository URL not found");
                return;
            }

            var headBranch = HeadBranch.Get(context);
            if (string.IsNullOrWhiteSpace(headBranch))
                headBranch = $"feature/{storyId}";

            // 1) CI gate — verify the head branch's build is green before merging.
            if (verifyCi)
            {
                var ciOk = await IsCiGreenAsync(story.RepositoryUrl, headBranch);
                if (!ciOk)
                {
                    _logger?.LogWarning(
                        "CI is not green for PR #{PRNumber} (branch {Branch}); routing to escalation",
                        prNumber, headBranch);
                    await FailAsync(context, strategy,
                        $"CI is not green for branch '{headBranch}'; merge blocked pending senior review.");
                    return;
                }
            }

            // 2) Strategy-aware merge with a single retry on failure.
            var mergeStrategyWire = strategy.ToString().ToLowerInvariant(); // squash | merge | rebase
            var mergeResult = await _integrationService!.MergeGitHubPullRequestAsync(
                story.RepositoryUrl, prNumber, mergeStrategyWire);

            if (!mergeResult.Success)
            {
                _logger?.LogWarning(
                    "Merge failed for PR #{PRNumber}: {Error}. Retrying once.",
                    prNumber, mergeResult.Error);

                mergeResult = await _integrationService.MergeGitHubPullRequestAsync(
                    story.RepositoryUrl, prNumber, mergeStrategyWire);

                if (!mergeResult.Success)
                {
                    _logger?.LogWarning(
                        "Merge retry failed for PR #{PRNumber}: {Error}; routing to escalation",
                        prNumber, mergeResult.Error);
                    await FailAsync(context, strategy,
                        $"Merge failed after retry: {mergeResult.Error}");
                    return;
                }
            }

            // 3) Delete the source branch (best-effort — never fails the merge).
            if (deleteBranch)
            {
                try
                {
                    await _integrationService.DeleteGitHubBranchAsync(story.RepositoryUrl, headBranch);
                    _logger?.LogInformation(
                        "Deleted source branch {Branch} after merging PR #{PRNumber}",
                        headBranch, prNumber);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Failed to delete source branch {Branch} after merge (merge still succeeded)",
                        headBranch);
                }
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

            // Notify junior — Story 38-3b: enqueue via the API seam (engine holds no
            // Slack credential); fire-and-forget, fail-soft.
            if (junior != null && !string.IsNullOrEmpty(junior.SlackId))
            {
                var iterationNote = totalIterations > 0
                    ? $" after {totalIterations} fix iteration(s)"
                    : "";

                await MediatedSlack.QueueDirectMessageAsync(
                    context,
                    junior.SlackId,
                    $"Your PR #{prNumber} has been approved and merged{iterationNote}! " +
                    "Great work on completing the code review process.",
                    "Success", "SendDirect", context.CancellationToken);
            }

            _logger?.LogInformation(
                "PR #{PRNumber} merged successfully for session {SessionId}, sha {MergeSha}",
                prNumber, sessionId, mergeResult.MergeSha);

            Result.Set(context, new ReviewMergeResult
            {
                Success = true,
                MergeSha = mergeResult.MergeSha,
                StrategyUsed = strategy
            });
            await context.CompleteActivityWithOutcomesAsync("Merged");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error merging PR #{PRNumber} for session {SessionId}", prNumber, sessionId);
            await FailAsync(context, strategy, $"Merge failed: {ex.Message}");
        }
    }

    private async ValueTask FailAsync(
        ActivityExecutionContext context, MergeStrategy strategy, string error)
    {
        Result.Set(context, new ReviewMergeResult
        {
            Success = false,
            StrategyUsed = strategy,
            Error = error
        });
        await context.CompleteActivityWithOutcomesAsync("Failed");
    }

    /// <summary>
    /// Returns true only when CI reports an unambiguously green build for the branch.
    /// Anything else (failure, pending, error, or an exception talking to CI) is treated
    /// as NOT-green — fail-closed: a PR never merges on an unknown CI state.
    /// </summary>
    private async Task<bool> IsCiGreenAsync(string repository, string branch)
    {
        try
        {
            var status = await _integrationService!.GetBuildStatusAsync(repository, branch);
            var s = status?.Status?.Trim().ToLowerInvariant();
            return s is "success" or "passed" or "passing" or "completed" or "green";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Failed to read CI status for branch {Branch}; treating as not-green", branch);
            return false;
        }
    }
}
