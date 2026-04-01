using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL.Models;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Bookmark-based activity that waits for a SingleIssueCycle to call back
/// with its result. Resumes when an external signal is received.
///
/// The callback payload is a CycleCallbackPayload with exit reason and issue details.
///
/// Outcomes:
///   - CycleCompleted: a cycle finished (success or failure)
///   - NoActiveCycles: all dispatched cycles have completed
/// </summary>
[Activity(
    "Tamma.ADL",
    "Wait for Cycle Callback",
    "Wait for a dispatched issue cycle to report its result",
    Kind = ActivityKind.Task
)]
[FlowNode("CycleCompleted", "NoActiveCycles")]
public class WaitForCycleCallbackActivity : TammaOutcomeActivity
{
    public override string? EventType => "ADL.CYCLE.CALLBACK";

    [Input(Description = "Number of currently active (dispatched) cycles")]
    public Input<int> ActiveCycles { get; set; } = new(0);

    [Output(Description = "Exit reason from the completed cycle")]
    public Output<string> ExitReason { get; set; } = default!;

    [Output(Description = "Issue number from the completed cycle")]
    public Output<int?> IssueNumber { get; set; } = default!;

    [Output(Description = "Whether the cycle succeeded")]
    public Output<bool> CycleSucceeded { get; set; } = default!;

    [JsonConstructor]
    public WaitForCycleCallbackActivity() { }

    public WaitForCycleCallbackActivity(ILogger<WaitForCycleCallbackActivity> logger)
    {
        Logger = logger;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var active = ActiveCycles.Get(context);

        if (active <= 0)
        {
            // No active cycles — nothing to wait for
            ExitReason.Set(context, "noActiveCycles");
            CycleSucceeded.Set(context, false);
            await context.CompleteActivityWithOutcomesAsync("NoActiveCycles");
            return;
        }

        // Create a bookmark and suspend — will be resumed by external callback
        context.CreateBookmark(new CreateBookmarkArgs
        {
            Callback = OnCycleCallbackReceived,
            BookmarkName = "WaitForCycleCallback",
            IncludeActivityInstanceId = false
        });
    }

    private async ValueTask OnCycleCallbackReceived(ActivityExecutionContext context)
    {
        // The callback payload comes from the external signal
        var payload = context.WorkflowInput.GetValueOrDefault("cycleResult") as IDictionary<string, object>;

        var reason = "unknown";
        int? issueNumber = null;

        if (payload != null)
        {
            if (payload.TryGetValue("exitReason", out var er))
                reason = er?.ToString() ?? "unknown";
            if (payload.TryGetValue("issueNumber", out var num) && num is int n)
                issueNumber = n;
        }

        var succeeded = reason == "success";

        ExitReason.Set(context, reason);
        IssueNumber.Set(context, issueNumber);
        CycleSucceeded.Set(context, succeeded);

        Logger?.LogInformation(
            "Cycle callback received: reason={Reason}, issue={Issue}, success={Success}",
            reason, issueNumber, succeeded);

        await context.CompleteActivityWithOutcomesAsync("CycleCompleted");
    }

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["activeCycles"] = ActiveCycles.Get(context),
    };

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["exitReason"] = ExitReason.Get(context),
        ["issueNumber"] = IssueNumber.Get(context),
        ["cycleSucceeded"] = CycleSucceeded.Get(context),
    };
}
