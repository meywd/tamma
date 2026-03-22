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
/// Bookmark-based blocking activity that suspends the workflow until the junior
/// pushes fixes. Creates a bookmark named "fixes-{sessionId}-{prNumber}-{iteration}"
/// and waits for an external signal carrying a <see cref="FixesSubmittedPayload"/>.
///
/// Outcomes:
///   - FixesReceived: junior pushed fixes
///   - TimedOut: no fixes within the timeout window
/// </summary>
[Activity(
    "Tamma.Review",
    "Wait For Fixes",
    "Wait for the junior developer to push fixes (bookmark-based)",
    Kind = ActivityKind.Task
)]
[FlowNode("FixesReceived", "TimedOut")]
public class WaitForFixesActivity : Activity
{
    private readonly ILogger<WaitForFixesActivity>? _logger;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<string> SessionId { get; set; } = default!;

    /// <summary>Pull request number</summary>
    [Input(Description = "Pull request number")]
    public Input<int> PRNumber { get; set; } = default!;

    /// <summary>Current fix iteration (1-based)</summary>
    [Input(Description = "Current fix iteration")]
    public Input<int> Iteration { get; set; } = default!;

    /// <summary>Timeout in hours for the junior to push fixes (default: 24)</summary>
    [Input(Description = "Fix timeout in hours", DefaultValue = 24)]
    public Input<int> TimeoutHours { get; set; } = new(24);

    /// <summary>The fixes payload received from the external signal</summary>
    [Output(Description = "Fixes submitted payload")]
    public Output<FixesSubmittedPayload?> FixesPayload { get; set; } = default!;

    [JsonConstructor]
    public WaitForFixesActivity() { }

    public WaitForFixesActivity(ILogger<WaitForFixesActivity> logger)
    {
        _logger = logger;
    }

    protected override void Execute(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var prNumber = PRNumber.Get(context);
        var iteration = Iteration.Get(context);
        var timeoutHours = TimeoutHours.Get(context);

        var bookmarkName = $"fixes-{sessionId}-{prNumber}-{iteration}";

        _logger?.LogInformation(
            "Creating fixes bookmark {BookmarkName}, timeout {TimeoutHours}h",
            bookmarkName, timeoutHours);

        // Store deadline for timeout check on resume
        context.SetVariable("FixDeadline", DateTime.UtcNow.AddHours(timeoutHours));

        // Create the bookmark — workflow suspends here
        context.CreateBookmark(
            new CreateBookmarkArgs
            {
                BookmarkName = bookmarkName,
                Payload = new { SessionId = sessionId, PRNumber = prNumber, Iteration = iteration },
                Callback = OnFixesReceivedAsync,
                AutoBurn = true
            });
    }

    private async ValueTask OnFixesReceivedAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var prNumber = PRNumber.Get(context);
        var iteration = Iteration.Get(context);

        // Check for timeout
        var deadline = context.GetVariable<DateTime>("FixDeadline");
        if (deadline != default && DateTime.UtcNow > deadline)
        {
            _logger?.LogWarning(
                "Fix submission timed out for PR #{PRNumber}, iteration {Iteration}",
                prNumber, iteration);

            FixesPayload.Set(context, null);
            await context.CompleteActivityWithOutcomesAsync("TimedOut");
            return;
        }

        // Extract the fixes payload from workflow input
        var input = context.WorkflowInput;

        var commitSha = input.TryGetValue("CommitSha", out var sha) ? sha?.ToString() : null;
        var message = input.TryGetValue("Message", out var msg) ? msg?.ToString() : null;

        var filesChanged = new List<string>();
        if (input.TryGetValue("FilesChanged", out var files) && files is IEnumerable<object> fileList)
        {
            filesChanged.AddRange(fileList.Select(f => f?.ToString() ?? ""));
        }

        var payload = new FixesSubmittedPayload
        {
            SessionId = sessionId,
            PRNumber = prNumber,
            Iteration = iteration,
            CommitSha = commitSha,
            FilesChanged = filesChanged,
            Message = message
        };

        FixesPayload.Set(context, payload);

        _logger?.LogInformation(
            "Fixes received for PR #{PRNumber}, iteration {Iteration}: {FileCount} files changed",
            prNumber, iteration, filesChanged.Count);

        await context.CompleteActivityWithOutcomesAsync("FixesReceived");
    }
}
