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
/// ELSA activity to monitor junior developer's implementation progress.
/// Detects stalls, circular behavior, and completion.
/// </summary>
[Activity(
    "Tamma.Mentorship",
    "Monitor Implementation",
    "Monitor junior developer's implementation progress and detect issues",
    Kind = ActivityKind.Task
)]
public class MonitorImplementationActivity : CodeActivity<ProgressOutput>
{
    private readonly ILogger<MonitorImplementationActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IIntegrationService? _integrationService;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>ID of the story being implemented</summary>
    [Input(Description = "ID of the story being implemented")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>ID of the junior developer</summary>
    [Input(Description = "ID of the junior developer")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Monitoring duration in minutes</summary>
    [Input(Description = "Monitoring duration in minutes", DefaultValue = 60)]
    public Input<int> MonitoringDuration { get; set; } = new(60);

    /// <summary>Check interval in minutes</summary>
    [Input(Description = "Check interval in minutes", DefaultValue = 5)]
    public Input<int> CheckInterval { get; set; } = new(5);

    [JsonConstructor]
    public MonitorImplementationActivity() { }

    public MonitorImplementationActivity(
        ILogger<MonitorImplementationActivity> logger,
        IMentorshipSessionRepository repository,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _repository = repository;
        _integrationService = integrationService;
    }

    /// <summary>
    /// Execute the monitoring activity
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var juniorId = JuniorId.Get(context);
        var monitoringDuration = MonitoringDuration.Get(context);
        var checkInterval = CheckInterval.Get(context);

        _logger?.LogInformation(
            "Starting implementation monitoring for junior {JuniorId} on story {StoryId}",
            juniorId, storyId);

        try
        {
            // Update session state
            await _repository!.UpdateStateAsync(sessionId, MentorshipState.MONITOR_PROGRESS);

            // Get story for context
            var story = await _repository.GetStoryByIdAsync(storyId);
            if (story == null)
            {
                _logger?.LogError("Story {StoryId} not found", storyId);
                context.SetResult(new ProgressOutput
                {
                    Status = ProgressStatus.Error,
                    NextState = MentorshipState.FAILED,
                    Message = $"Story {storyId} not found"
                });
                return;
            }

            // Collect progress data from integrations
            var progressData = await CollectProgressData(story.RepositoryUrl, juniorId, storyId);

            // Analyze progress
            var analysis = AnalyzeProgress(progressData);

            // Log progress event
            await _repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.ProgressUpdate,
                StateFrom = MentorshipState.START_IMPLEMENTATION,
                StateTo = MentorshipState.MONITOR_PROGRESS
            });

            _logger?.LogInformation(
                "Progress analysis for junior {JuniorId}: Status={Status}, Reason={Reason}",
                juniorId, analysis.Status, analysis.Reason);

            context.SetResult(analysis);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during progress monitoring for session {SessionId}", sessionId);

            context.SetResult(new ProgressOutput
            {
                Status = ProgressStatus.Error,
                NextState = MentorshipState.DIAGNOSE_BLOCKER,
                Message = ex.Message
            });
        }
    }

    private async Task<ImplementationProgress> CollectProgressData(string? repositoryUrl, string juniorId, string storyId)
    {
        var progress = new ImplementationProgress
        {
            StoryId = storyId,
            JuniorId = juniorId,
            Timestamp = DateTime.UtcNow
        };

        if (!string.IsNullOrEmpty(repositoryUrl))
        {
            try
            {
                // Get recent commits
                var commits = await _integrationService!.GetGitHubCommitsAsync(
                    repositoryUrl,
                    $"feature/{storyId}",
                    DateTime.UtcNow.AddHours(-1));

                progress.Commits = commits;
                progress.LastActivity = commits.Any()
                    ? commits.Max(c => c.Timestamp)
                    : DateTime.UtcNow.AddHours(-2); // Assume stale if no commits

                // Get file changes
                var fileChanges = await _integrationService.GetGitHubFileChangesAsync(
                    repositoryUrl,
                    $"feature/{storyId}");
                progress.FileChanges = fileChanges;

                // Get build status
                var buildStatus = await _integrationService.GetBuildStatusAsync(
                    repositoryUrl,
                    $"feature/{storyId}");
                progress.BuildStatus = buildStatus.Status;

                // Get test results
                var testResults = await _integrationService.TriggerTestsAsync(
                    repositoryUrl,
                    $"feature/{storyId}");
                progress.TestResults = testResults;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to collect GitHub progress data");
            }
        }

        return progress;
    }

    private ProgressOutput AnalyzeProgress(ImplementationProgress progress)
    {
        // Check for no activity (stalled)
        var timeSinceLastActivity = DateTime.UtcNow - progress.LastActivity;
        if (timeSinceLastActivity.TotalMinutes > 15)
        {
            return new ProgressOutput
            {
                Status = ProgressStatus.Stalled,
                NextState = MentorshipState.DIAGNOSE_BLOCKER,
                Reason = $"No activity for {timeSinceLastActivity.TotalMinutes:F0} minutes",
                LastActivity = progress.LastActivity
            };
        }

        // Check for circular behavior (repeated failures)
        if (progress.TestResults != null && progress.TestResults.FailedTests > 0)
        {
            var failedTestNames = progress.TestResults.FailedTestDetails
                .Select(t => t.TestName)
                .ToList();

            // Simplified circular detection - in production would track history
            if (failedTestNames.Count > 3)
            {
                return new ProgressOutput
                {
                    Status = ProgressStatus.Circular,
                    NextState = MentorshipState.DETECT_PATTERN,
                    Reason = "Repeated test failures detected",
                    Pattern = $"Same {failedTestNames.Count} tests failing repeatedly",
                    RepetitionCount = failedTestNames.Count
                };
            }
        }

        // Check for build failure
        if (progress.BuildStatus == "Failed")
        {
            return new ProgressOutput
            {
                Status = ProgressStatus.Stalled,
                NextState = MentorshipState.DIAGNOSE_BLOCKER,
                Reason = "Build is failing",
                LastActivity = progress.LastActivity
            };
        }

        // Check for completion
        if (progress.BuildStatus == "Success" &&
            progress.TestResults != null &&
            progress.TestResults.FailedTests == 0 &&
            progress.TestResults.PassedTests > 0)
        {
            return new ProgressOutput
            {
                Status = ProgressStatus.Complete,
                NextState = MentorshipState.QUALITY_GATE_CHECK,
                Reason = "Implementation complete - all tests passing",
                CompletionPercentage = 100
            };
        }

        // Check progress rate
        var commitCount = progress.Commits?.Count ?? 0;
        if (commitCount < 1)
        {
            return new ProgressOutput
            {
                Status = ProgressStatus.Slowing,
                NextState = MentorshipState.PROVIDE_GUIDANCE,
                Reason = "Low commit activity",
                LastActivity = progress.LastActivity
            };
        }

        // Steady progress
        return new ProgressOutput
        {
            Status = ProgressStatus.Steady,
            NextState = MentorshipState.MONITOR_PROGRESS,
            Reason = "Progress is steady",
            CompletionPercentage = CalculateCompletionPercentage(progress),
            LastActivity = progress.LastActivity
        };
    }

    private int CalculateCompletionPercentage(ImplementationProgress progress)
    {
        var score = 0;

        // Commits contribute 30%
        var commitCount = progress.Commits?.Count ?? 0;
        score += Math.Min(30, commitCount * 10);

        // File changes contribute 30%
        var fileCount = progress.FileChanges?.Count ?? 0;
        score += Math.Min(30, fileCount * 5);

        // Build status contributes 20%
        if (progress.BuildStatus == "Success")
            score += 20;
        else if (progress.BuildStatus == "InProgress")
            score += 10;

        // Test status contributes 20%
        if (progress.TestResults != null)
        {
            var passRate = progress.TestResults.TotalTests > 0
                ? (double)progress.TestResults.PassedTests / progress.TestResults.TotalTests
                : 0;
            score += (int)(passRate * 20);
        }

        return Math.Min(95, score); // Cap at 95% until full completion confirmed
    }
}

/// <summary>
/// Progress status enum
/// </summary>
public enum ProgressStatus
{
    Steady,
    Slowing,
    Stalled,
    Circular,
    Complete,
    Error
}

/// <summary>
/// Output model for progress monitoring
/// </summary>
public class ProgressOutput
{
    public ProgressStatus Status { get; set; }
    public MentorshipState NextState { get; set; }
    public string? Reason { get; set; }
    public string? Pattern { get; set; }
    public int RepetitionCount { get; set; }
    public int CompletionPercentage { get; set; }
    public DateTime LastActivity { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Implementation progress data collected from integrations
/// </summary>
public class ImplementationProgress
{
    public string StoryId { get; set; } = string.Empty;
    public string JuniorId { get; set; } = string.Empty;
    public List<GitHubCommit>? Commits { get; set; }
    public List<GitHubFileChange>? FileChanges { get; set; }
    public DateTime LastActivity { get; set; }
    public string? BuildStatus { get; set; }
    public TestRunResult? TestResults { get; set; }
    public DateTime Timestamp { get; set; }
}
