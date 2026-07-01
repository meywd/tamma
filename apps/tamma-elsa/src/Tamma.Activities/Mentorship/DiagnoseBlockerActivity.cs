using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.LlmCall;
using Tamma.Core.Enums;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Mentorship;

/// <summary>
/// ELSA activity to diagnose and classify blockers that are preventing progress.
/// Analyzes junior's behavior, recent activity, and context to determine blocker type.
/// </summary>
[Activity(
    "Tamma.Mentorship",
    "Diagnose Blocker",
    "Analyze and classify what is blocking the junior developer's progress",
    Kind = ActivityKind.Task
)]
public class DiagnoseBlockerActivity : CodeActivity<BlockerDiagnosisOutput>
{
    private readonly ILogger<DiagnoseBlockerActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IIntegrationService? _integrationService;
    private readonly IAnalyticsService? _analyticsService;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>ID of the story being worked on</summary>
    [Input(Description = "ID of the story being worked on")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>ID of the junior developer</summary>
    [Input(Description = "ID of the junior developer")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Additional context about the blocker (optional)</summary>
    [Input(Description = "Additional context about the blocker")]
    public Input<string?> BlockerContext { get; set; } = default!;

    [JsonConstructor]
    public DiagnoseBlockerActivity() { }

    public DiagnoseBlockerActivity(
        ILogger<DiagnoseBlockerActivity> logger,
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
    /// Execute the blocker diagnosis activity
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var juniorId = JuniorId.Get(context);
        var blockerContext = BlockerContext.Get(context);

        _logger?.LogInformation(
            "Diagnosing blocker for junior {JuniorId} on story {StoryId}",
            juniorId, storyId);

        try
        {
            // Update session state
            await _repository!.UpdateStateAsync(sessionId, MentorshipState.DIAGNOSE_BLOCKER);

            // Get story and junior information
            var story = await _repository.GetStoryByIdAsync(storyId);
            var junior = await _repository.GetJuniorByIdAsync(juniorId);
            var session = await _repository.GetByIdAsync(sessionId);

            if (story == null || junior == null || session == null)
            {
                _logger?.LogError("Required entities not found for blocker diagnosis");
                context.SetResult(new BlockerDiagnosisOutput
                {
                    BlockerType = BlockerType.UNKNOWN,
                    Severity = BlockerSeverity.High,
                    NextState = MentorshipState.ESCALATE_TO_SENIOR,
                    Message = "Unable to diagnose - missing session data"
                });
                return;
            }

            // Collect diagnostic data
            var diagnosticData = await CollectDiagnosticData(story, junior, storyId, juniorId);

            // Analyze patterns from analytics
            var patterns = await _analyticsService!.DetectPatternsAsync(juniorId);

            // Diagnose the blocker type
            var diagnosis = AnalyzeBlocker(diagnosticData, patterns, blockerContext);

            // Log the diagnosis event
            await _repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.BlockerDiagnosed,
                StateFrom = session.CurrentState,
                StateTo = MentorshipState.DIAGNOSE_BLOCKER
            });

            // Record analytics
            await _analyticsService.RecordMetricAsync(
                sessionId,
                "blocker_diagnosed",
                (double)diagnosis.BlockerType,
                "blocker_type");

            _logger?.LogInformation(
                "Blocker diagnosed for junior {JuniorId}: Type={Type}, Severity={Severity}",
                juniorId, diagnosis.BlockerType, diagnosis.Severity);

            // Notify junior about diagnosis
            if (!string.IsNullOrEmpty(junior.SlackId))
            {
                await NotifyJunior(context, junior.SlackId, diagnosis);
            }

            context.SetResult(diagnosis);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during blocker diagnosis for session {SessionId}", sessionId);

            context.SetResult(new BlockerDiagnosisOutput
            {
                BlockerType = BlockerType.UNKNOWN,
                Severity = BlockerSeverity.High,
                NextState = MentorshipState.ESCALATE_TO_SENIOR,
                Message = $"Diagnosis failed: {ex.Message}"
            });
        }
    }

    private async Task<DiagnosticData> CollectDiagnosticData(
        Tamma.Core.Entities.Story story,
        Tamma.Core.Entities.JuniorDeveloper junior,
        string storyId,
        string juniorId)
    {
        var data = new DiagnosticData
        {
            StoryComplexity = story.Complexity,
            JuniorSkillLevel = junior.SkillLevel,
            TimeSinceLastActivity = TimeSpan.Zero
        };

        // Collect GitHub activity if repository configured
        if (!string.IsNullOrEmpty(story.RepositoryUrl))
        {
            try
            {
                var commits = await _integrationService!.GetGitHubCommitsAsync(
                    story.RepositoryUrl,
                    $"feature/{storyId}",
                    DateTime.UtcNow.AddHours(-24));

                data.RecentCommitCount = commits.Count;
                data.LastCommitTime = commits.Any() ? commits.Max(c => c.Timestamp) : null;

                if (data.LastCommitTime.HasValue)
                {
                    data.TimeSinceLastActivity = DateTime.UtcNow - data.LastCommitTime.Value;
                }

                var buildStatus = await _integrationService.GetBuildStatusAsync(
                    story.RepositoryUrl,
                    $"feature/{storyId}");
                data.BuildStatus = buildStatus.Status;
                data.BuildError = buildStatus.Error;

                var testResults = await _integrationService.TriggerTestsAsync(
                    story.RepositoryUrl,
                    $"feature/{storyId}");
                data.FailingTestCount = testResults.FailedTests;
                data.FailingTests = testResults.FailedTestDetails
                    .Select(t => t.TestName)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to collect GitHub diagnostic data");
            }
        }

        return data;
    }

    private BlockerDiagnosisOutput AnalyzeBlocker(
        DiagnosticData data,
        List<BehaviorPattern> patterns,
        string? additionalContext)
    {
        // Priority-based blocker detection

        // 1. Check for build failures
        if (data.BuildStatus == "Failed" && !string.IsNullOrEmpty(data.BuildError))
        {
            var isSyntaxError = data.BuildError.Contains("CS") ||
                               data.BuildError.Contains("syntax") ||
                               data.BuildError.Contains("expected");

            return new BlockerDiagnosisOutput
            {
                BlockerType = isSyntaxError
                    ? BlockerType.TECHNICAL_KNOWLEDGE_GAP
                    : BlockerType.ENVIRONMENT_ISSUE,
                Severity = BlockerSeverity.Medium,
                Description = $"Build is failing: {data.BuildError}",
                RootCause = isSyntaxError
                    ? "Syntax or compilation error in code"
                    : "Build configuration or environment issue",
                SuggestedAction = isSyntaxError
                    ? "Review compiler errors and fix syntax issues"
                    : "Check build configuration and dependencies",
                NextState = MentorshipState.PROVIDE_HINT,
                Message = "Build failure detected"
            };
        }

        // 2. Check for repeated test failures (circular behavior)
        if (data.FailingTestCount > 3)
        {
            return new BlockerDiagnosisOutput
            {
                BlockerType = BlockerType.TESTING_CHALLENGE,
                Severity = BlockerSeverity.Medium,
                Description = $"Multiple tests failing: {string.Join(", ", data.FailingTests.Take(3))}",
                RootCause = "Struggling to understand test requirements or implementation logic",
                SuggestedAction = "Provide test-specific guidance and debugging tips",
                NextState = MentorshipState.PROVIDE_GUIDANCE,
                Message = "Testing challenges detected"
            };
        }

        // 3. Check for prolonged inactivity
        if (data.TimeSinceLastActivity.TotalMinutes > 30)
        {
            // Check behavior patterns for more context
            var concerningPatterns = patterns.Where(p => p.Type == PatternType.Concerning).ToList();

            if (concerningPatterns.Any(p => p.Name.Contains("frustration") || p.Name.Contains("confusion")))
            {
                return new BlockerDiagnosisOutput
                {
                    BlockerType = BlockerType.MOTIVATION_ISSUE,
                    Severity = BlockerSeverity.Medium,
                    Description = "Prolonged inactivity with signs of frustration",
                    RootCause = "Junior may be feeling overwhelmed or stuck",
                    SuggestedAction = "Reach out with encouragement and offer direct assistance",
                    NextState = MentorshipState.PROVIDE_ASSISTANCE,
                    Message = "Motivation blocker detected"
                };
            }

            return new BlockerDiagnosisOutput
            {
                BlockerType = BlockerType.REQUIREMENTS_UNCLEAR,
                Severity = BlockerSeverity.Medium,
                Description = $"No activity for {data.TimeSinceLastActivity.TotalMinutes:F0} minutes",
                RootCause = "Junior may be unsure how to proceed",
                SuggestedAction = "Check in and clarify next steps",
                NextState = MentorshipState.PROVIDE_HINT,
                Message = "Inactivity blocker detected"
            };
        }

        // 4. Check skill level vs story complexity mismatch
        if (data.StoryComplexity - data.JuniorSkillLevel >= 2)
        {
            return new BlockerDiagnosisOutput
            {
                BlockerType = BlockerType.TECHNICAL_KNOWLEDGE_GAP,
                Severity = BlockerSeverity.High,
                Description = "Story complexity exceeds junior's current skill level",
                RootCause = $"Skill level {data.JuniorSkillLevel} vs complexity {data.StoryComplexity}",
                SuggestedAction = "Provide additional learning resources or pair programming",
                NextState = MentorshipState.PROVIDE_ASSISTANCE,
                Message = "Skill gap blocker detected"
            };
        }

        // 5. Check additional context if provided
        if (!string.IsNullOrEmpty(additionalContext))
        {
            var contextLower = additionalContext.ToLower();

            if (contextLower.Contains("dependency") || contextLower.Contains("package") || contextLower.Contains("npm"))
            {
                return new BlockerDiagnosisOutput
                {
                    BlockerType = BlockerType.DEPENDENCY_ISSUE,
                    Severity = BlockerSeverity.Medium,
                    Description = additionalContext,
                    RootCause = "External dependency or package issue",
                    SuggestedAction = "Help resolve dependency configuration",
                    NextState = MentorshipState.PROVIDE_ASSISTANCE,
                    Message = "Dependency blocker detected"
                };
            }

            if (contextLower.Contains("architecture") || contextLower.Contains("design") || contextLower.Contains("structure"))
            {
                return new BlockerDiagnosisOutput
                {
                    BlockerType = BlockerType.ARCHITECTURE_CONFUSION,
                    Severity = BlockerSeverity.Medium,
                    Description = additionalContext,
                    RootCause = "Uncertainty about code architecture or design patterns",
                    SuggestedAction = "Provide architectural guidance and examples",
                    NextState = MentorshipState.PROVIDE_GUIDANCE,
                    Message = "Architecture blocker detected"
                };
            }
        }

        // 6. Default - unable to determine specific blocker
        return new BlockerDiagnosisOutput
        {
            BlockerType = BlockerType.UNKNOWN,
            Severity = BlockerSeverity.Low,
            Description = "Unable to determine specific blocker type",
            RootCause = "Insufficient diagnostic data",
            SuggestedAction = "Reach out to junior for more context",
            NextState = MentorshipState.PROVIDE_HINT,
            Message = "Blocker type undetermined"
        };
    }

    private static async Task NotifyJunior(
        ActivityExecutionContext context, string slackId, BlockerDiagnosisOutput diagnosis)
    {
        var message = $@"**Tamma: Blocker Detected**

I've noticed you might be stuck. Here's what I've identified:

*Type:* {diagnosis.BlockerType.ToString().Replace("_", " ")}
*Description:* {diagnosis.Description}

*Suggested Next Step:* {diagnosis.SuggestedAction}

Don't worry - this is a normal part of learning! Reply if you need more help.";

        // Story 38-3b — enqueue the diagnosis DM via the API seam (engine holds no
        // Slack credential); fire-and-forget, fail-soft.
        await MediatedSlack.QueueDirectMessageAsync(
            context, slackId, message, "Warning", "SendDirect", context.CancellationToken);
    }
}

/// <summary>
/// Diagnostic data collected for blocker analysis
/// </summary>
public class DiagnosticData
{
    public int StoryComplexity { get; set; }
    public int JuniorSkillLevel { get; set; }
    public int RecentCommitCount { get; set; }
    public DateTime? LastCommitTime { get; set; }
    public TimeSpan TimeSinceLastActivity { get; set; }
    public string? BuildStatus { get; set; }
    public string? BuildError { get; set; }
    public int FailingTestCount { get; set; }
    public List<string> FailingTests { get; set; } = new();
}

/// <summary>
/// Blocker severity levels
/// </summary>
public enum BlockerSeverity
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// Output model for blocker diagnosis
/// </summary>
public class BlockerDiagnosisOutput
{
    public BlockerType BlockerType { get; set; }
    public BlockerSeverity Severity { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? RootCause { get; set; }
    public string? SuggestedAction { get; set; }
    public MentorshipState NextState { get; set; }
    public string? Message { get; set; }
    public List<string> RelatedResources { get; set; } = new();
}
