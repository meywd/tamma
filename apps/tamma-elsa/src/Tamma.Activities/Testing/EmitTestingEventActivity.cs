using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.Testing;

/// <summary>
/// Emits a <c>TEST.*</c> / <c>GATE.*</c> DCB event (completeness audit 2026-06-22,
/// <c>Testing.md</c> §Missing #3) for the built-out <c>testing-pipeline</c> workflow by
/// appending a <see cref="TammaEvent"/> to the workflow's <c>tamma:events</c> transient
/// list via <see cref="TammaEventEmitter.Emit"/>. The merged engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that list <i>durably</i>
/// to the tenant <c>domain_events</c> store after this activity runs — no DB / repository
/// dependency is held by this activity (none is registered in the Elsa engine, the same
/// reason <see cref="EmitTddDebugEventActivity"/> uses the emitter rather than a direct
/// <c>IEventRepository</c>).
///
/// <para>The pipeline emits these at: CI trigger (success/failed), results received, each
/// gate evaluation, each fix commit (committed / no-op), and every terminal
/// (pass / fail / escalated). The failure / timeout / no-op / escalation events are
/// error-status (see <see cref="TestingEvents.StatusForEvent"/>) so a degraded terminal is
/// a LOUD audit row, never a silent false success.</para>
/// </summary>
[Activity(
    "Tamma.Testing",
    "Emit Testing Event",
    "Emit a TEST.* / GATE.* DCB event for the testing-pipeline audit trail",
    Kind = ActivityKind.Task
)]
public class EmitTestingEventActivity : Activity
{
    private readonly ILogger<EmitTestingEventActivity>? _logger;

    [Input(Description = "Event type — TEST.CI_TRIGGERED.* / TEST.RESULTS_RECEIVED / TEST.CI_TIMED_OUT / GATE.EVALUATED / GATE.AUTOFIX_* / GATE.PASSED / GATE.FAILED / GATE.ESCALATED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Mentorship / testing session id (empty when unknown)")]
    public Input<string?> SessionId { get; set; } = new((string?)null);

    [Input(Description = "Repository URL or owner/repo")]
    public Input<string?> Repository { get; set; } = new((string?)null);

    [Input(Description = "Branch under test")]
    public Input<string?> Branch { get; set; } = new((string?)null);

    [Input(Description = "CI run id (empty before/without a run)")]
    public Input<string?> RunId { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Current auto-fix attempt number (0 before the first fix)")]
    public Input<int> Attempt { get; set; } = new(0);

    [Input(Description = "Max auto-fix attempts for the loop")]
    public Input<int> MaxAttempts { get; set; } = new(0);

    [Input(Description = "Quality-gate outcome (AllPass / MinorIssues / MajorIssues / Critical; empty when N/A)")]
    public Input<string?> Outcome { get; set; } = new((string?)null);

    [Input(Description = "Overall quality score 0-100 (negative → omitted)")]
    public Input<double> Score { get; set; } = new(-1d);

    [Input(Description = "Junior skill level 1-5 (0 → omitted)")]
    public Input<int> SkillLevel { get; set; } = new(0);

    [Input(Description = "Files changed by an auto-fix commit (negative → omitted)")]
    public Input<int> FilesChanged { get; set; } = new(-1);

    [Input(Description = "Terminal escalation reason (critical-quality-failure / retry-budget-exhausted / ci-timeout / ci-trigger-failed / autofix-no-op; empty on non-terminal events)")]
    public Input<string?> EscalationReason { get; set; } = new((string?)null);

    [Input(Description = "Underlying failure detail surfaced on a loud terminal (empty otherwise; never raw secrets)")]
    public Input<string?> ErrorDetail { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitTestingEventActivity() { }

    public EmitTestingEventActivity(ILogger<EmitTestingEventActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.GetOrDefault(context) ?? TestingEvents.GateFailed;

        var evt = BuildTammaEvent(
            type,
            sessionId: SessionId.GetOrDefault(context),
            repository: Repository.GetOrDefault(context),
            branch: Branch.GetOrDefault(context),
            runId: RunId.GetOrDefault(context),
            tenantId: TestingEvents.ParseTenantId(TenantId.GetOrDefault(context)),
            attempt: Attempt.GetOrDefault(context),
            maxAttempts: MaxAttempts.GetOrDefault(context),
            outcome: Outcome.GetOrDefault(context),
            score: Score.GetOrDefault(context),
            skillLevel: SkillLevel.GetOrDefault(context),
            filesChanged: FilesChanged.GetOrDefault(context),
            escalationReason: EscalationReason.GetOrDefault(context),
            errorDetail: ErrorDetail.GetOrDefault(context));

        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for session {Session} repo {Repo} (run={Run}, attempt={Attempt}/{Max}, outcome={Outcome})",
            type, SessionId.GetOrDefault(context), Repository.GetOrDefault(context), RunId.GetOrDefault(context),
            Attempt.GetOrDefault(context), MaxAttempts.GetOrDefault(context), Outcome.GetOrDefault(context));

        await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete (see EmitEscalationEventActivity precedent); without this the workflow hangs here forever
        return;
    }

    /// <summary>
    /// Map the testing-pipeline inputs onto a <see cref="TammaEvent"/> expressed as the
    /// engine's transient-list event so the merged drain persists it. Tags carry the
    /// queryable DCB index keys (<c>sessionId</c> / <c>repository</c> / <c>branch</c> /
    /// <c>runId</c> / <c>attempt</c> / <c>tenantId</c>); <c>Data</c> carries the gate
    /// payload (<c>outcome</c> / <c>score</c> / <c>skillLevel</c> / <c>filesChanged</c> /
    /// <c>escalationReason</c> / <c>errorDetail</c>). Status is driven off the event type
    /// (failed / timeout / no-op / escalated → error) so a degraded terminal is never
    /// recorded as a false success. Pure (no Elsa context); exposed for unit testing.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string? sessionId,
        string? repository,
        string? branch,
        string? runId,
        Guid? tenantId,
        int attempt,
        int maxAttempts,
        string? outcome,
        double score,
        int skillLevel,
        int filesChanged,
        string? escalationReason,
        string? errorDetail)
    {
        var tags = new Dictionary<string, object?>
        {
            ["attempt"] = attempt.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(sessionId)) tags["sessionId"] = sessionId;
        if (!string.IsNullOrWhiteSpace(repository)) tags["repository"] = repository;
        if (!string.IsNullOrWhiteSpace(branch)) tags["branch"] = branch;
        if (!string.IsNullOrWhiteSpace(runId)) tags["runId"] = runId;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>
        {
            ["attempt"] = attempt,
            ["maxAttempts"] = maxAttempts,
        };
        if (!string.IsNullOrWhiteSpace(outcome)) data["outcome"] = outcome;
        if (score >= 0) data["score"] = score;
        if (skillLevel > 0) data["skillLevel"] = skillLevel;
        if (filesChanged >= 0) data["filesChanged"] = filesChanged;
        if (!string.IsNullOrWhiteSpace(escalationReason)) data["escalationReason"] = escalationReason;
        if (!string.IsNullOrWhiteSpace(errorDetail)) data["errorDetail"] = errorDetail;

        return new TammaEvent
        {
            EventType = type,
            Status = TestingEvents.StatusForEvent(type),
            Tags = tags,
            Data = data,
        };
    }
}
