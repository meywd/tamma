using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.Debug;

/// <summary>
/// Emits a <c>DEBUG.*</c> DCB event (completeness audit 2026-06-22,
/// <c>Debugging.md</c> §Missing #8) for the built-out <c>debugging</c> sub-workflow by
/// appending a <see cref="TammaEvent"/> to the workflow's <c>tamma:events</c> transient
/// list via <see cref="TammaEventEmitter.Emit"/>. The merged engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that list <i>durably</i>
/// to the tenant <c>domain_events</c> store after this activity runs — no DB / repository
/// dependency is held by this activity (none is registered in the Elsa engine, the same
/// reason <see cref="Tamma.Activities.Testing.EmitTestingEventActivity"/> uses the emitter
/// rather than a direct <c>IEventRepository</c>).
///
/// <para>The workflow emits these at: session start (after classify), each diagnosis
/// (success / failed), each hypothesis selection, each fix attempt (with a success flag),
/// each test verdict (passed / failed), an invalid regression test, and every terminal
/// (resolved / escalated). The diagnosis-failed, tests-failed, regression-invalid and
/// escalated events are error-status (see <see cref="DebugEvents.StatusForEvent"/>) so a
/// degraded terminal is a LOUD audit row, never a silent false success.</para>
/// </summary>
[Activity(
    "Tamma.Debug",
    "Emit Debug Event",
    "Emit a DEBUG.* DCB event for the debugging sub-workflow audit trail",
    Kind = ActivityKind.Task
)]
public class EmitDebugEventActivity : Activity
{
    private readonly ILogger<EmitDebugEventActivity>? _logger;

    [Input(Description = "Event type — DEBUG.SESSION.STARTED / .DIAGNOSIS.SUCCESS|FAILED / .HYPOTHESIS.SELECTED / .FIX.ATTEMPTED / .TESTS.PASSED|FAILED / .REGRESSION_TEST.INVALID / .RESOLVED.SUCCESS / .ESCALATED.FAILED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Debug session id")]
    public Input<string?> SessionId { get; set; } = new((string?)null);

    [Input(Description = "Story id being debugged")]
    public Input<string?> StoryId { get; set; } = new((string?)null);

    [Input(Description = "Debug context mode (TddFailure / RuntimeError / BugInvestigation)")]
    public Input<string?> Mode { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Current debug iteration (0 before the first iteration)")]
    public Input<int> Iteration { get; set; } = new(0);

    [Input(Description = "Max iterations budget for the loop (0 → omitted)")]
    public Input<int> MaxIterations { get; set; } = new(0);

    [Input(Description = "Selected/root-cause hypothesis description (empty when N/A)")]
    public Input<string?> Hypothesis { get; set; } = new((string?)null);

    [Input(Description = "Whether the most recent fix dispatch reported success (only meaningful on DEBUG.FIX.ATTEMPTED)")]
    public Input<bool> FixSucceeded { get; set; } = new(false);

    [Input(Description = "Terminal escalation reason (no-hypothesis-selected / max-iterations-reached / regression-test-did-not-reproduce-bug; empty on non-terminal events)")]
    public Input<string?> Reason { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitDebugEventActivity() { }

    public EmitDebugEventActivity(ILogger<EmitDebugEventActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.Get(context) ?? DebugEvents.Escalated;

        var evt = BuildTammaEvent(
            type,
            sessionId: SessionId.Get(context),
            storyId: StoryId.Get(context),
            mode: Mode.Get(context),
            tenantId: DebugEvents.ParseTenantId(TenantId.Get(context)),
            iteration: Iteration.Get(context),
            maxIterations: MaxIterations.Get(context),
            hypothesis: Hypothesis.Get(context),
            fixSucceeded: FixSucceeded.Get(context),
            reason: Reason.Get(context));

        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for session {Session} story {Story} (mode={Mode}, iteration={Iteration}/{Max}, reason={Reason})",
            type, SessionId.Get(context), StoryId.Get(context), Mode.Get(context),
            Iteration.Get(context), MaxIterations.Get(context), Reason.Get(context));

        return default;
    }

    /// <summary>
    /// Map the debug inputs onto a <see cref="TammaEvent"/> expressed as the engine's
    /// transient-list event so the merged drain persists it. Tags carry the queryable DCB
    /// index keys (<c>sessionId</c> / <c>storyId</c> / <c>mode</c> / <c>tenantId</c> /
    /// <c>iteration</c>); <c>Data</c> carries the loop payload (<c>iteration</c> /
    /// <c>maxIterations</c> / <c>hypothesis</c> / <c>fixSucceeded</c> / <c>reason</c>).
    /// Status is driven off the event type (diagnosis-failed / tests-failed /
    /// regression-invalid / escalated → error) so a degraded terminal is never recorded as
    /// a false success. <c>fixSucceeded</c> is only emitted on <c>DEBUG.FIX.ATTEMPTED</c>
    /// so a failed-but-continuing fix is visible without being a loud error. Pure (no Elsa
    /// context); exposed for unit testing the mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string? sessionId,
        string? storyId,
        string? mode,
        Guid? tenantId,
        int iteration,
        int maxIterations,
        string? hypothesis,
        bool fixSucceeded,
        string? reason)
    {
        var tags = new Dictionary<string, object?>
        {
            ["iteration"] = iteration.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(sessionId)) tags["sessionId"] = sessionId;
        if (!string.IsNullOrWhiteSpace(storyId)) tags["storyId"] = storyId;
        if (!string.IsNullOrWhiteSpace(mode)) tags["mode"] = mode;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>
        {
            ["iteration"] = iteration,
        };
        if (maxIterations > 0) data["maxIterations"] = maxIterations;
        if (!string.IsNullOrWhiteSpace(hypothesis)) data["hypothesis"] = hypothesis;
        if (type == DebugEvents.FixAttempted) data["fixSucceeded"] = fixSucceeded;
        if (!string.IsNullOrWhiteSpace(reason)) data["reason"] = reason;

        return new TammaEvent
        {
            EventType = type,
            Status = DebugEvents.StatusForEvent(type),
            Tags = tags,
            Data = data,
        };
    }
}
