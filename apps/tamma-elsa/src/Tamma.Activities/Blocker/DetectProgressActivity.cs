using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.Blocker.Models;

namespace Tamma.Activities.Blocker;

/// <summary>
/// Bookmark-based activity that waits for the junior developer to make progress.
/// Progress can be detected via:
///   - New commits on the branch
///   - CI triggered with new results
///   - Junior sends "resolved" signal via API/Slack
///   - File changes in relevant files
///
/// The workflow suspends at this point and is resumed externally when progress
/// is detected or the wait time expires.
/// </summary>
[Activity(
    "Tamma.Blocker",
    "Detect Progress",
    "Wait for and detect junior's progress after resolution attempt",
    Kind = ActivityKind.Task
)]
public class DetectProgressActivity : Activity
{
    private readonly ILogger<DetectProgressActivity>? _logger;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story ID</summary>
    [Input(Description = "Story identifier")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Junior developer ID</summary>
    [Input(Description = "Junior developer identifier")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Current resolution level being waited on</summary>
    [Input(Description = "Current resolution level")]
    public Input<string> CurrentLevel { get; set; } = default!;

    /// <summary>Wait time in minutes before escalating</summary>
    [Input(Description = "Wait time in minutes before escalating", DefaultValue = 15)]
    public Input<int> WaitTimeMinutes { get; set; } = new(15);

    /// <summary>Whether progress was detected</summary>
    [Output(Description = "Whether progress was detected")]
    public Output<bool> ProgressDetected { get; set; } = default!;

    /// <summary>Details of the progress detection result</summary>
    [Output(Description = "Progress detection result details")]
    public Output<ProgressDetectionResult> Result { get; set; } = default!;

    [JsonConstructor]
    public DetectProgressActivity() { }

    public DetectProgressActivity(ILogger<DetectProgressActivity> logger)
    {
        _logger = logger;
    }

    protected override void Execute(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var juniorId = JuniorId.Get(context);
        var currentLevel = CurrentLevel.Get(context);
        var waitTimeMinutes = WaitTimeMinutes.Get(context);

        _logger?.LogInformation(
            "Waiting for progress detection: Session={SessionId}, Level={Level}, Timeout={Timeout}min",
            sessionId, currentLevel, waitTimeMinutes);

        var payload = new ProgressDetectionPayload
        {
            SessionId = sessionId,
            StoryId = storyId,
            JuniorId = juniorId,
            CurrentLevel = Enum.TryParse<ResolutionLevel>(currentLevel, out var level)
                ? level
                : ResolutionLevel.Hint,
            WaitTimeMinutes = waitTimeMinutes
        };

        // Create bookmark — workflow suspends here until external resume
        context.CreateBookmark(new CreateBookmarkArgs
        {
            BookmarkName = $"blocker-progress-{sessionId}-{currentLevel}",
            Payload = payload,
            Callback = OnResumeAsync,
            AutoBurn = true
        });
    }

    private async ValueTask OnResumeAsync(ActivityExecutionContext context)
    {
        var input = context.WorkflowInput;

        var progressDetected = input.TryGetValue("ProgressDetected", out var pd)
            && pd is true;
        var progressType = input.TryGetValue("ProgressType", out var pt)
            ? pt?.ToString()
            : null;
        var details = input.TryGetValue("Details", out var d)
            ? d?.ToString()
            : null;

        var result = new ProgressDetectionResult
        {
            ProgressDetected = progressDetected,
            ProgressType = progressType,
            Details = details
        };

        _logger?.LogInformation(
            "Progress detection resumed: Detected={Detected}, Type={Type}",
            progressDetected, progressType);

        context.Set(ProgressDetected, progressDetected);
        context.Set(Result, result);

        await context.CompleteActivityAsync();
    }
}
