using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Sets the exit reason for the orchestrator with event emission.
/// Replaces raw SetOutput for audit trail.
/// </summary>
[Activity(
    "Tamma.ADL",
    "Set Exit Reason",
    "Record why the orchestrator is stopping",
    Kind = ActivityKind.Task
)]
public class SetExitReasonActivity : TammaActivity
{
    public override string? EventType => "ADL.EXIT";

    [Input(Description = "Exit reason (noIssues, limitsReached, error)")]
    public Input<string> Reason { get; set; } = default!;

    [JsonConstructor]
    public SetExitReasonActivity() { }

    public SetExitReasonActivity(ILogger<SetExitReasonActivity> logger)
    {
        Logger = logger;
    }

    protected override void Run(ActivityExecutionContext context)
    {
        var reason = Reason.Get(context);
        context.WorkflowExecutionContext.Output["exitReason"] = reason;
        Logger?.LogInformation("ADL Orchestrator exiting: {Reason}", reason);
    }

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["reason"] = Reason.Get(context),
    };
}
