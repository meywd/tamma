using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Core.Enums;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Mentorship;

/// <summary>
/// ELSA activity to handle the final merge and completion of a mentorship session.
/// Merges the PR, updates records, generates reports, and updates skill profiles.
/// </summary>
[Activity(
    "Tamma.Mentorship",
    "Merge Complete",
    "Handle merge and completion of the mentorship session",
    Kind = ActivityKind.Task
)]
public class MergeCompleteActivity : CodeActivity<MergeCompleteOutput>
{
    private readonly ILogger<MergeCompleteActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IIntegrationService? _integrationService;
    private readonly IAnalyticsService? _analyticsService;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>ID of the story being completed</summary>
    [Input(Description = "ID of the story being completed")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>ID of the junior developer</summary>
    [Input(Description = "ID of the junior developer")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Pull request number to merge</summary>
    [Input(Description = "PR number to merge")]
    public Input<int> PullRequestNumber { get; set; } = default!;

    /// <summary>Whether to auto-merge or just prepare for merge</summary>
    [Input(Description = "Auto-merge the PR", DefaultValue = true)]
    public Input<bool> AutoMerge { get; set; } = new(true);

    [JsonConstructor]
    public MergeCompleteActivity() { }

    public MergeCompleteActivity(
        ILogger<MergeCompleteActivity> logger,
        IMentorshipSessionRepository repository,
        IIntegrationService integrationService,
        IAnalyticsService analyticsService)
    {
        _logger = logger;
        _repository = repository;
        _integrationService = integrationService;
        _analyticsService = analyticsService;
    }

    /// <summary>
    /// Execute the merge and completion activity
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var juniorId = JuniorId.Get(context);
        var prNumber = PullRequestNumber.Get(context);
        var autoMerge = AutoMerge.Get(context);

        _logger?.LogInformation(
            "Starting merge and completion for session {SessionId}, PR #{PrNumber}",
            sessionId, prNumber);

        try
        {
            // Update session state
            await _repository!.UpdateStateAsync(sessionId, MentorshipState.MERGE_AND_COMPLETE);

            // Get required entities
            var session = await _repository.GetByIdAsync(sessionId);
            var story = await _repository.GetStoryByIdAsync(storyId);
            var junior = await _repository.GetJuniorByIdAsync(juniorId);

            if (session == null || story == null || junior == null)
            {
                context.SetResult(new MergeCompleteOutput
                {
                    Success = false,
                    Message = "Missing required entities for completion"
                });
                return;
            }

            // Step 1: Merge the PR
            GitHubMergeResult? mergeResult = null;
            if (autoMerge && !string.IsNullOrEmpty(story.RepositoryUrl))
            {
                mergeResult = await _integrationService!.MergeGitHubPullRequestAsync(
                    story.RepositoryUrl,
                    prNumber);

                if (!mergeResult.Success)
                {
                    _logger?.LogWarning("PR merge failed: {Error}", mergeResult.Error);
                    // Don't fail the whole process - PR can be merged manually
                }
            }

            // Step 2: Generate session report
            var report = await GenerateSessionReport(session, story, junior);

            // Step 3: Calculate skill updates
            var skillUpdate = await CalculateSkillUpdate(session, junior, report);

            // Step 4: Update junior's skill profile
            if (skillUpdate.ShouldUpdateSkill)
            {
                junior.SkillLevel = skillUpdate.NewSkillLevel;
                await _repository.UpdateJuniorAsync(junior);

                _logger?.LogInformation(
                    "Updated junior {JuniorId} skill level from {OldLevel} to {NewLevel}",
                    juniorId, skillUpdate.OldSkillLevel, skillUpdate.NewSkillLevel);
            }

            // Step 5: Update JIRA ticket if configured
            if (!string.IsNullOrEmpty(story.JiraTicketId))
            {
                await _integrationService!.UpdateJiraTicketAsync(
                    story.JiraTicketId,
                    new JiraTicketUpdate
                    {
                        Status = "Done",
                        Comment = $"Completed through Tamma mentorship. PR #{prNumber} merged."
                    });
            }

            // Step 6: Update session as completed
            await _repository.UpdateStateAsync(sessionId, MentorshipState.COMPLETED);
            await _repository.UpdateStatusAsync(sessionId, Tamma.Core.Entities.SessionStatus.Completed);

            // Step 7: Log completion event
            await _repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.SessionCompleted,
                StateFrom = MentorshipState.MERGE_AND_COMPLETE,
                StateTo = MentorshipState.COMPLETED
            });

            // Step 8: Record analytics
            await RecordCompletionAnalytics(sessionId, session, report);

            // Step 9: Notify junior about completion
            if (!string.IsNullOrEmpty(junior.SlackId))
            {
                await NotifyCompletion(junior.SlackId, story, report, skillUpdate);
            }

            _logger?.LogInformation(
                "Mentorship session {SessionId} completed successfully",
                sessionId);

            context.SetResult(new MergeCompleteOutput
            {
                Success = true,
                MergeSha = mergeResult?.MergeSha,
                MergeSuccessful = mergeResult?.Success ?? false,
                Report = report,
                SkillUpdate = skillUpdate,
                Message = "Mentorship session completed successfully!"
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during merge and completion for session {SessionId}", sessionId);

            context.SetResult(new MergeCompleteOutput
            {
                Success = false,
                Message = $"Completion failed: {ex.Message}"
            });
        }
    }

    private async Task<SessionReport> GenerateSessionReport(
        Tamma.Core.Entities.MentorshipSession session,
        Tamma.Core.Entities.Story story,
        Tamma.Core.Entities.JuniorDeveloper junior)
    {
        // Get all events for the session
        var events = await _repository!.GetEventsBySessionIdAsync(session.Id);

        // Get session metrics
        var metrics = await _analyticsService!.GetSessionMetricsAsync(session.Id);

        // Calculate statistics
        var sessionDuration = session.CompletedAt.HasValue
            ? session.CompletedAt.Value - session.CreatedAt
            : DateTime.UtcNow - session.CreatedAt;

        var stateTransitions = events
            .Where(e => e.StateFrom.HasValue && e.StateTo.HasValue)
            .GroupBy(e => $"{e.StateFrom}->{e.StateTo}")
            .ToDictionary(g => g.Key, g => g.Count());

        var blockerCount = events.Count(e => e.EventType == Tamma.Core.Entities.EventTypes.BlockerDiagnosed);
        var guidanceCount = events.Count(e => e.EventType == Tamma.Core.Entities.EventTypes.GuidanceProvided);

        // Identify strengths and areas for improvement
        var strengths = new List<string>();
        var improvements = new List<string>();

        if (blockerCount == 0)
            strengths.Add("Completed without major blockers");
        else if (blockerCount > 3)
            improvements.Add("Consider breaking down complex tasks further");

        if (sessionDuration.TotalHours < story.EstimatedHours * 0.8)
            strengths.Add("Completed ahead of estimated time");
        else if (sessionDuration.TotalHours > story.EstimatedHours * 1.5)
            improvements.Add("Time management could be improved");

        var qualityEvents = events.Where(e => e.EventType == Tamma.Core.Entities.EventTypes.QualityGateRun).ToList();
        if (qualityEvents.Count <= 2)
            strengths.Add("Good code quality - few iterations needed");
        else if (qualityEvents.Count > 4)
            improvements.Add("Consider writing tests earlier in development");

        return new SessionReport
        {
            SessionId = session.Id,
            StoryId = story.Id,
            StoryTitle = story.Title,
            JuniorId = junior.Id,
            JuniorName = junior.Name,
            StartTime = session.CreatedAt,
            EndTime = DateTime.UtcNow,
            Duration = sessionDuration,
            TotalEvents = events.Count,
            StateTransitions = stateTransitions,
            BlockerCount = blockerCount,
            GuidanceProvided = guidanceCount,
            EstimatedHours = story.EstimatedHours ?? 0,
            ActualHours = sessionDuration.TotalHours,
            Strengths = strengths,
            AreasForImprovement = improvements,
            OverallScore = CalculateOverallScore(sessionDuration, story.EstimatedHours ?? 0, blockerCount, guidanceCount)
        };
    }

    private double CalculateOverallScore(TimeSpan duration, int estimatedHours, int blockerCount, int guidanceCount)
    {
        var score = 100.0;

        // Time factor (up to -20 points)
        var timeRatio = duration.TotalHours / Math.Max(1, estimatedHours);
        if (timeRatio > 1.5)
            score -= Math.Min(20, (timeRatio - 1.5) * 10);
        else if (timeRatio < 0.8)
            score += 5; // Bonus for efficiency

        // Blocker factor (up to -15 points)
        score -= Math.Min(15, blockerCount * 3);

        // Guidance factor (each guidance is expected, but too many indicates struggle)
        if (guidanceCount > 5)
            score -= Math.Min(10, (guidanceCount - 5) * 2);

        return Math.Max(0, Math.Min(100, score));
    }

    private async Task<SkillUpdateResult> CalculateSkillUpdate(
        Tamma.Core.Entities.MentorshipSession session,
        Tamma.Core.Entities.JuniorDeveloper junior,
        SessionReport report)
    {
        // Get skill recommendation from analytics
        var recommendation = await _analyticsService!.CalculateSkillLevelAsync(junior.Id);

        var result = new SkillUpdateResult
        {
            OldSkillLevel = junior.SkillLevel,
            NewSkillLevel = junior.SkillLevel,
            ShouldUpdateSkill = false,
            Reason = "No change warranted"
        };

        // Consider upgrade if score is high and consistent
        if (report.OverallScore >= 85 && recommendation.Confidence > 0.7)
        {
            if (recommendation.RecommendedLevel > junior.SkillLevel)
            {
                result.NewSkillLevel = Math.Min(5, junior.SkillLevel + 1);
                result.ShouldUpdateSkill = true;
                result.Reason = "Excellent performance across multiple sessions";
            }
        }
        // Consider if multiple good sessions without upgrade
        else if (report.OverallScore >= 75 && recommendation.RecommendedLevel > junior.SkillLevel)
        {
            // Only upgrade if analytics strongly suggests it
            if (recommendation.Confidence > 0.85)
            {
                result.NewSkillLevel = recommendation.RecommendedLevel;
                result.ShouldUpdateSkill = true;
                result.Reason = "Consistent good performance indicates growth";
            }
        }

        result.SkillGaps = recommendation.SkillGaps;
        result.RecommendedLearning = recommendation.RecommendedLearning;

        return result;
    }

    private async Task RecordCompletionAnalytics(
        Guid sessionId,
        Tamma.Core.Entities.MentorshipSession session,
        SessionReport report)
    {
        await _analyticsService!.RecordMetricAsync(sessionId, "session_completed", 1);
        await _analyticsService.RecordMetricAsync(sessionId, "duration_hours", report.ActualHours, "hours");
        await _analyticsService.RecordMetricAsync(sessionId, "overall_score", report.OverallScore, "score");
        await _analyticsService.RecordMetricAsync(sessionId, "blocker_count", report.BlockerCount, "count");
        await _analyticsService.RecordMetricAsync(sessionId, "guidance_count", report.GuidanceProvided, "count");

        await _analyticsService.RecordJuniorMetricAsync(
            session.JuniorId,
            "sessions_completed",
            1,
            "count");
    }

    private async Task NotifyCompletion(
        string slackId,
        Tamma.Core.Entities.Story story,
        SessionReport report,
        SkillUpdateResult skillUpdate)
    {
        var durationStr = report.Duration.TotalHours >= 1
            ? $"{report.Duration.TotalHours:F1} hours"
            : $"{report.Duration.TotalMinutes:F0} minutes";

        var scoreEmoji = report.OverallScore >= 90 ? "star2" :
                        report.OverallScore >= 75 ? "star" :
                        report.OverallScore >= 60 ? "thumbsup" : "muscle";

        var message = $@"**Tamma: Mentorship Session Complete!** :tada:

Congratulations on completing **{story.Title}**!

**Session Summary**
- Duration: {durationStr}
- Overall Score: {report.OverallScore:F0}/100 :{scoreEmoji}:
- Blockers Overcome: {report.BlockerCount}
- Guidance Sessions: {report.GuidanceProvided}";

        if (report.Strengths.Any())
        {
            message += $@"

**Strengths** :muscle:
{string.Join("\n", report.Strengths.Select(s => $"- {s}"))}";
        }

        if (report.AreasForImprovement.Any())
        {
            message += $@"

**Growth Opportunities** :seedling:
{string.Join("\n", report.AreasForImprovement.Select(a => $"- {a}"))}";
        }

        if (skillUpdate.ShouldUpdateSkill)
        {
            message += $@"

**Skill Level Update** :chart_with_upwards_trend:
Your skill level has been updated from {skillUpdate.OldSkillLevel} to {skillUpdate.NewSkillLevel}!
{skillUpdate.Reason}";
        }

        if (skillUpdate.RecommendedLearning.Any())
        {
            message += $@"

**Recommended Learning**
{string.Join("\n", skillUpdate.RecommendedLearning.Take(3).Select(l => $"- {l}"))}";
        }

        message += @"

Great work! Ready for your next challenge?";

        await _integrationService!.SendSlackDirectMessageAsync(slackId, message);
    }
}

/// <summary>
/// Session completion report
/// </summary>
public class SessionReport
{
    public Guid SessionId { get; set; }
    public string StoryId { get; set; } = string.Empty;
    public string StoryTitle { get; set; } = string.Empty;
    public string JuniorId { get; set; } = string.Empty;
    public string JuniorName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public int TotalEvents { get; set; }
    public Dictionary<string, int> StateTransitions { get; set; } = new();
    public int BlockerCount { get; set; }
    public int GuidanceProvided { get; set; }
    public int EstimatedHours { get; set; }
    public double ActualHours { get; set; }
    public List<string> Strengths { get; set; } = new();
    public List<string> AreasForImprovement { get; set; } = new();
    public double OverallScore { get; set; }
}

/// <summary>
/// Skill update result
/// </summary>
public class SkillUpdateResult
{
    public int OldSkillLevel { get; set; }
    public int NewSkillLevel { get; set; }
    public bool ShouldUpdateSkill { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<string> SkillGaps { get; set; } = new();
    public List<string> RecommendedLearning { get; set; } = new();
}

/// <summary>
/// Output model for merge and completion activity
/// </summary>
public class MergeCompleteOutput
{
    public bool Success { get; set; }
    public string? MergeSha { get; set; }
    public bool MergeSuccessful { get; set; }
    public SessionReport? Report { get; set; }
    public SkillUpdateResult? SkillUpdate { get; set; }
    public string? Message { get; set; }
}
