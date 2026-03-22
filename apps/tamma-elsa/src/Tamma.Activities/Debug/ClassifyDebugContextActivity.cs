using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.Debug.Models;

namespace Tamma.Activities.Debug;

/// <summary>
/// Routes the debugging workflow based on the debug entry context.
/// Determines context-gathering emphasis for each mode:
///   - TddFailure: emphasize test output and implementation code
///   - RuntimeError: emphasize stack traces and recent changes
///   - BugInvestigation: emphasize issue description and reproduction steps
/// </summary>
[Activity(
    "Tamma.Debug",
    "Classify Debug Context",
    "Route based on debug mode: TDD failure, runtime error, or bug investigation",
    Kind = ActivityKind.Task
)]
[FlowNode("TddFailure", "RuntimeError", "BugInvestigation")]
public class ClassifyDebugContextActivity : Activity
{
    private readonly ILogger<ClassifyDebugContextActivity>? _logger;

    /// <summary>Debug context mode</summary>
    [Input(Description = "Debug context: TddFailure, RuntimeError, or BugInvestigation")]
    public Input<string> DebugContextMode { get; set; } = default!;

    /// <summary>Session ID for logging</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [JsonConstructor]
    public ClassifyDebugContextActivity() { }

    public ClassifyDebugContextActivity(ILogger<ClassifyDebugContextActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var mode = DebugContextMode.Get(context);
        var sessionId = SessionId.Get(context);

        _logger?.LogInformation(
            "Classifying debug context for session {SessionId}: mode={Mode}",
            sessionId, mode);

        var outcome = mode switch
        {
            "TddFailure" => "TddFailure",
            "RuntimeError" => "RuntimeError",
            "BugInvestigation" => "BugInvestigation",
            _ => "RuntimeError" // Default fallback
        };

        _logger?.LogInformation(
            "Debug context classified as {Outcome} for session {SessionId}",
            outcome, sessionId);

        await context.CompleteActivityWithOutcomesAsync(outcome);
    }
}
