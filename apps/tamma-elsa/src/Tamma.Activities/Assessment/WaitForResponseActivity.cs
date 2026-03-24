using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.Assessment.Models;

namespace Tamma.Activities.Assessment;

/// <summary>
/// Bookmark-based activity that pauses the workflow until the junior responds
/// or a timeout occurs. The bookmark name follows the pattern:
/// assessment-{sessionId}-{attemptNumber}
///
/// Timeout varies by skill level:
///   Level 1-2: 10 minutes
///   Level 3:    7 minutes
///   Level 4-5:  5 minutes
///
/// Outcomes: "Responded" when a response is received, "Timeout" on expiry.
/// </summary>
[Activity(
    "Tamma.Assessment",
    "Wait For Response",
    "Pause workflow until junior responds or timeout occurs",
    Kind = ActivityKind.Task
)]
[FlowNode("Responded", "Timeout")]
public class WaitForResponseActivity : Activity
{
    private readonly ILogger<WaitForResponseActivity>? _logger;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Assessment attempt number</summary>
    [Input(Description = "Assessment attempt number", DefaultValue = 1)]
    public Input<int> AttemptNumber { get; set; } = new(1);

    /// <summary>Junior's skill level for timeout calculation</summary>
    [Input(Description = "Junior skill level (1-5)", DefaultValue = 3)]
    public Input<int> SkillLevel { get; set; } = new(3);

    /// <summary>The junior's response text (set on bookmark resume)</summary>
    [Output(Description = "Junior's response text")]
    public Output<string> JuniorResponse { get; set; } = default!;

    /// <summary>Whether the response was received or timed out</summary>
    [Output(Description = "Whether a response was received")]
    public Output<bool> ResponseReceived { get; set; } = default!;

    [JsonConstructor]
    public WaitForResponseActivity() { }

    public WaitForResponseActivity(
        ILogger<WaitForResponseActivity> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override void Execute(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var attemptNumber = AttemptNumber.Get(context);
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);

        var bookmarkName = $"assessment-{sessionId}-{attemptNumber}";
        var timeoutMinutes = GetTimeoutMinutes(skillLevel);

        _logger?.LogInformation(
            "Waiting for assessment response: bookmark={BookmarkName}, timeout={TimeoutMinutes}min, skillLevel={SkillLevel}",
            bookmarkName, timeoutMinutes, skillLevel);

        var payload = new AssessmentBookmarkPayload
        {
            SessionId = sessionId,
            AttemptNumber = attemptNumber,
            BookmarkName = bookmarkName
        };

        // Create the bookmark that pauses the workflow
        context.CreateBookmark(
            new CreateBookmarkArgs
            {
                BookmarkName = bookmarkName,
                Callback = OnResponseReceivedAsync,
                AutoBurn = true
            });
    }

    /// <summary>
    /// Callback invoked when the bookmark is resumed (junior responds)
    /// </summary>
    private async ValueTask OnResponseReceivedAsync(ActivityExecutionContext context)
    {
        var input = context.WorkflowInput;

        // Check if this is a timeout signal
        var isTimeout = input.TryGetValue("Timeout", out var timeoutVal)
            && timeoutVal is true;

        if (isTimeout)
        {
            _logger?.LogInformation("Assessment response timed out for session");

            JuniorResponse.Set(context, string.Empty);
            ResponseReceived.Set(context, false);

            await context.CompleteActivityWithOutcomesAsync("Timeout");
            return;
        }

        // Extract the response
        var response = string.Empty;
        if (input.TryGetValue("Response", out var responseVal))
        {
            response = responseVal?.ToString() ?? string.Empty;
        }

        _logger?.LogInformation(
            "Assessment response received, length={ResponseLength}",
            response.Length);

        JuniorResponse.Set(context, response);
        ResponseReceived.Set(context, true);

        await context.CompleteActivityWithOutcomesAsync("Responded");
    }

    /// <summary>
    /// Get timeout duration in minutes based on skill level.
    /// Reads from configuration or uses defaults.
    /// </summary>
    private int GetTimeoutMinutes(int skillLevel)
    {
        var configKey = $"Assessment:TimeoutMinutes:{skillLevel}";
        var configValue = _configuration?[configKey];
        if (int.TryParse(configValue, out var configured))
        {
            return configured;
        }

        return skillLevel switch
        {
            1 => 10,
            2 => 10,
            3 => 7,
            4 => 5,
            5 => 5,
            _ => 7
        };
    }
}
