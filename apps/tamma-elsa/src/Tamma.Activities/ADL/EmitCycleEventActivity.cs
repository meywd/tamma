using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Emits a <c>CYCLE.*</c> DCB event (completeness audit 2026-06-22,
/// <c>SingleIssueCycle.md</c> §Missing #1 / Phase A) for the built-out
/// <c>single-issue-cycle</c> workflow by appending a <see cref="TammaEvent"/> to the
/// workflow's <c>tamma:events</c> transient list via <see cref="TammaEventEmitter.Emit"/>.
/// The merged engine event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>)
/// flushes that list <i>durably</i> to the tenant <c>domain_events</c> store after this
/// activity runs — no DB / repository dependency is held by this activity (none is
/// registered in the Elsa engine, the same reason <see cref="EmitTddDebugEventActivity"/>
/// uses the emitter rather than a direct <c>IEventRepository</c>).
///
/// <para>The cycle emits these at: <c>STARTED</c> (after validate, before context),
/// <c>STEP_FAILED</c> (the shared fail-the-cycle sink — error-status, with the failing
/// <c>stepId</c> + the underlying detail), <c>COMPLETED</c> (success terminal), and
/// <c>FAILED</c> (the loud failure terminal). The failure / step-failed events are
/// error-status (see <see cref="CycleEvents.StatusForEvent"/>) so a degraded terminal is
/// a LOUD audit row, never a silent false success.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Emit Cycle Event",
    "Emit a CYCLE.* DCB event for the single-issue-cycle audit trail",
    Kind = ActivityKind.Task
)]
public class EmitCycleEventActivity : Activity
{
    private readonly ILogger<EmitCycleEventActivity>? _logger;

    [Input(Description = "Event type — CYCLE.STARTED / CYCLE.STEP_FAILED / CYCLE.COMPLETED / CYCLE.FAILED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Issue number driving the cycle (0 when unknown)")]
    public Input<int> IssueNumber { get; set; } = new(0);

    [Input(Description = "Repository in owner/repo format")]
    public Input<string?> Repository { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Failing step id on a CYCLE.STEP_FAILED event (empty otherwise)")]
    public Input<string?> StepId { get; set; } = new((string?)null);

    [Input(Description = "Underlying failure detail surfaced on a loud terminal (empty otherwise; never raw secrets)")]
    public Input<string?> ErrorDetail { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitCycleEventActivity() { }

    public EmitCycleEventActivity(ILogger<EmitCycleEventActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.GetOrDefault(context) ?? CycleEvents.Failed;

        // 2026-08-13 (found by the engine-driven E2E): the OPTIONAL inputs must
        // be read with GetOrDefault. A literal-null Input default is DROPPED by
        // the workflow-definition store's JSON round-trip, so an unwired input
        // materializes as null and Input.Get throws "<name> is required." —
        // which faulted EVERY CYCLE.STARTED/COMPLETED emit (the workflow only
        // wires StepId/ErrorDetail on the failure emits).
        var evt = BuildTammaEvent(
            type,
            issueNumber: IssueNumber.GetOrDefault(context),
            repository: Repository.GetOrDefault(context),
            tenantId: CycleEvents.ParseTenantId(TenantId.GetOrDefault(context)),
            stepId: StepId.GetOrDefault(context),
            errorDetail: ErrorDetail.GetOrDefault(context));

        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for issue #{Issue} repo {Repo} (step={Step})",
            type, IssueNumber.GetOrDefault(context), Repository.GetOrDefault(context),
            StepId.GetOrDefault(context));

        await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete (see EmitEscalationEventActivity precedent); without this the workflow hangs here forever
        return;
    }

    /// <summary>
    /// Map the cycle inputs onto a <see cref="TammaEvent"/> expressed as the engine's
    /// transient-list event so the merged drain persists it. Tags carry the queryable DCB
    /// index keys (<c>issueId</c> / <c>issueNumber</c> / <c>repository</c> /
    /// <c>tenantId</c> / <c>stepId</c>); <c>Data</c> carries the payload (<c>stepId</c> /
    /// <c>errorDetail</c>). Status is driven off the event type (step-failed / failed →
    /// error) so a degraded terminal is never recorded as a false success. Pure (no Elsa
    /// context); exposed for unit testing the mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        int issueNumber,
        string? repository,
        Guid? tenantId,
        string? stepId,
        string? errorDetail)
    {
        var tags = new Dictionary<string, object?>();
        if (issueNumber > 0)
        {
            tags["issueId"] = issueNumber.ToString();
            tags["issueNumber"] = issueNumber.ToString();
        }
        if (!string.IsNullOrWhiteSpace(repository)) tags["repository"] = repository;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");
        if (!string.IsNullOrWhiteSpace(stepId)) tags["stepId"] = stepId;

        var data = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(stepId)) data["stepId"] = stepId;
        if (!string.IsNullOrWhiteSpace(errorDetail)) data["errorDetail"] = errorDetail;

        return new TammaEvent
        {
            EventType = type,
            Status = CycleEvents.StatusForEvent(type),
            Tags = tags,
            Data = data,
        };
    }
}
