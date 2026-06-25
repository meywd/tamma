using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.Debug;

/// <summary>
/// AC4 / completeness audit 2026-06-22 (<c>Debugging.md</c> §Missing #11) — the durable
/// guard that bounds the debug context-collection Fork/Join so a hung collector
/// (a never-returning GitHub / test integration call) cannot suspend the workflow
/// forever. Runs as a parallel fork branch racing the 5 collectors: it arms a DURABLE
/// <see cref="Elsa.Extensions.DelayActivityExecutionContextExtensions.DelayFor(ActivityExecutionContext, System.TimeSpan, ExecuteActivityDelegate)"/>
/// bookmark (EF-persisted, re-armed by <c>Elsa.Scheduling</c> across a host restart — NOT
/// an in-memory <c>IWorkflowScheduler</c> timer) for <c>Debugging:ContextCollectionTimeoutSeconds</c>
/// (default 15s). On fire it takes the <c>TimedOut</c> outcome so the flowchart proceeds to
/// serialization with whatever collector outputs completed (partial context) instead of
/// hanging.
///
/// <para>The downstream serialization step is guarded by the workflow's
/// <c>contextGatherDone</c> bool so the FIRST of (join-completed / this-timeout) to reach
/// it wins and the second is short-circuited — the collectors and the existing
/// <c>FlowJoin(WaitAll)</c> are unchanged. A non-positive timeout disables the guard
/// (wait for the join only).</para>
/// </summary>
[Activity(
    "Tamma.Debug",
    "Context Collection Timeout",
    "Durable timeout that bounds the debug context Fork/Join (proceed with partial context on timeout)",
    Kind = ActivityKind.Task
)]
[FlowNode("Armed", "TimedOut")]
public class ContextCollectionTimeoutActivity : Activity
{
    private readonly ILogger<ContextCollectionTimeoutActivity>? _logger;
    private readonly IConfiguration? _configuration;

    /// <summary>Debug session id (for log correlation)</summary>
    [Input(Description = "Debug session id")]
    public Input<string> SessionId { get; set; } = new(string.Empty);

    [JsonConstructor]
    public ContextCollectionTimeoutActivity() { }

    public ContextCollectionTimeoutActivity(
        ILogger<ContextCollectionTimeoutActivity> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Resolve the configured context-collection timeout (<c>Debugging:ContextCollectionTimeoutSeconds</c>),
    /// defaulting to 15s and flooring a non-positive value to 0 (disabled). Pure; exposed
    /// for unit testing the config read.
    /// </summary>
    public static int ResolveTimeoutSeconds(IConfiguration? configuration)
    {
        var raw = configuration?["Debugging:ContextCollectionTimeoutSeconds"];
        if (int.TryParse(raw, out var parsed))
            return parsed < 0 ? 0 : parsed;
        return 15;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context) ?? string.Empty;
        var timeoutSeconds = ResolveTimeoutSeconds(_configuration);

        if (timeoutSeconds <= 0)
        {
            // Guard disabled — this branch completes immediately (the Armed outcome is a
            // no-op sink) without arming a delay, so the join (WaitAll) is the only path.
            _logger?.LogInformation(
                "Context-collection timeout guard disabled (ContextCollectionTimeoutSeconds<=0) for session {SessionId}",
                sessionId);
            await context.CompleteActivityWithOutcomesAsync("Armed");
            return;
        }

        _logger?.LogInformation(
            "Arming durable context-collection timeout ({TimeoutSeconds}s) for session {SessionId}",
            timeoutSeconds, sessionId);

        // Durable timeout bookmark — re-armed by Elsa.Scheduling across a restart.
        // The activity suspends here; OnTimeoutAsync resumes it with the TimedOut outcome.
        context.DelayFor(TimeSpan.FromSeconds(timeoutSeconds), OnTimeoutAsync);
    }

    private async ValueTask OnTimeoutAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context) ?? string.Empty;
        _logger?.LogWarning(
            "Context-collection window expired (durable timeout) for session {SessionId} — proceeding with partial context",
            sessionId);
        await context.CompleteActivityWithOutcomesAsync("TimedOut");
    }
}
