using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.Blocker;

/// <summary>
/// Emits a <c>BLOCKER.*</c> DCB event (completeness audit 2026-06-22,
/// <c>BlockerDiagnosis.md</c> §Missing #3, 7-1G AC9) for the <c>blocker-diagnosis</c>
/// sub-workflow by appending a <see cref="TammaEvent"/> to the workflow's
/// <c>tamma:events</c> transient list via <see cref="TammaEventEmitter.Emit"/>. The
/// merged engine event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>)
/// flushes that list <i>durably</i> to the tenant <c>domain_events</c> store after this
/// activity runs — the same pattern <see cref="Tamma.Activities.ADL.EmitBranchEventActivity"/>
/// and <see cref="Tamma.Activities.ADL.EmitPrEventActivity"/> use. No activity holds a DB /
/// repository dependency of its own (none is registered in the Elsa engine — a directly
/// injected <c>IEventRepository</c> would be inert and silently drop every event).
///
/// <para>On the terminal transitions it also records the AC9 OTel metrics via
/// <see cref="BlockerMetrics"/> (<c>blocker.total</c> on diagnosed, and
/// <c>blocker.resolved</c> / <c>blocker.escalated</c> / <c>blocker.timed_out</c> +
/// the <c>blocker.resolution_time</c> histogram on the matching terminal).</para>
/// </summary>
[Activity(
    "Tamma.Blocker",
    "Emit Blocker Event",
    "Emit a BLOCKER.* DCB event for the blocker-diagnosis audit trail",
    Kind = ActivityKind.Task
)]
public class EmitBlockerEventActivity : Activity
{
    private readonly ILogger<EmitBlockerEventActivity>? _logger;

    [Input(Description = "Event type — BLOCKER.DIAGNOSED.SUCCESS / .RESOLUTION_ATTEMPTED / .PROGRESS_DETECTED / .PROGRESS_TIMED_OUT / .ESCALATED / .RESOLVED / .TIMED_OUT")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Mentorship session id")]
    public Input<string?> SessionId { get; set; } = new((string?)null);

    [Input(Description = "Story id the junior is blocked on")]
    public Input<string?> StoryId { get; set; } = new((string?)null);

    [Input(Description = "Junior developer id")]
    public Input<string?> JuniorId { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Classified blocker type (8 categories)")]
    public Input<string?> BlockerType { get; set; } = new((string?)null);

    [Input(Description = "Blocker severity")]
    public Input<string?> Severity { get; set; } = new((string?)null);

    [Input(Description = "Resolution level for this attempt (Hint/Guidance/Assistance/Escalation)")]
    public Input<string?> Level { get; set; } = new((string?)null);

    [Input(Description = "Attempt number (1-based) for a RESOLUTION_ATTEMPTED event")]
    public Input<int> Attempt { get; set; } = new(0);

    [Input(Description = "Diagnosis confidence 0..1 (DIAGNOSED events)")]
    public Input<double> Confidence { get; set; } = new(0d);

    [Input(Description = "Progress type detail (PROGRESS_DETECTED events)")]
    public Input<string?> ProgressType { get; set; } = new((string?)null);

    [Input(Description = "Total resolution time in seconds (terminal events)")]
    public Input<double> ResolutionTimeSeconds { get; set; } = new(0d);

    [JsonConstructor]
    public EmitBlockerEventActivity() { }

    public EmitBlockerEventActivity(ILogger<EmitBlockerEventActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.GetOrDefault(context) ?? BlockerEvents.DiagnosedFailed;
        var sessionId = SessionId.GetOrDefault(context);
        var storyId = StoryId.GetOrDefault(context);
        var juniorId = JuniorId.GetOrDefault(context);
        var tenantRaw = TenantId.GetOrDefault(context);
        var tenantId = BlockerEvents.ParseTenantId(tenantRaw);
        var blockerType = BlockerType.GetOrDefault(context);
        var severity = Severity.GetOrDefault(context);
        var level = Level.GetOrDefault(context);
        var attempt = Attempt.GetOrDefault(context);
        var confidence = Confidence.GetOrDefault(context);
        var progressType = ProgressType.GetOrDefault(context);
        var resolutionSeconds = ResolutionTimeSeconds.GetOrDefault(context);

        // Metrics first — they must fire on the matching transition regardless of the
        // (best-effort) durable event append. Tenant tag uses the parsed canonical form
        // (null → "platform") so per-tenant perf data stays tenant-scoped (Epic 32).
        var tenantTag = tenantId?.ToString("D");
        RecordMetric(type, blockerType, tenantTag, level, resolutionSeconds);

        var evt = BuildTammaEvent(
            type, sessionId, storyId, juniorId, tenantId, blockerType, severity,
            level, attempt, confidence, progressType, resolutionSeconds);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for session {Session} story {Story} (level={Level}, attempt={Attempt})",
            type, sessionId, storyId, level, attempt);

        await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete (see EmitEscalationEventActivity precedent); without this the workflow hangs here forever
        return;
    }

    private static void RecordMetric(
        string type, string? blockerType, string? tenantTag, string? level, double resolutionSeconds)
    {
        var rt = TimeSpan.FromSeconds(resolutionSeconds);
        switch (type)
        {
            case BlockerEvents.DiagnosedSuccess:
                BlockerMetrics.RecordDiagnosed(blockerType, tenantTag);
                break;
            case BlockerEvents.Resolved:
                BlockerMetrics.RecordResolved(blockerType, tenantTag, level, rt);
                break;
            case BlockerEvents.Escalated:
                BlockerMetrics.RecordEscalated(blockerType, tenantTag, rt);
                break;
            case BlockerEvents.TimedOut:
                BlockerMetrics.RecordTimedOut(blockerType, tenantTag, rt);
                break;
        }
    }

    /// <summary>
    /// Map the blocker event inputs onto a <see cref="TammaEvent"/> expressed as the
    /// engine's transient-list event so the merged drain persists it. Tags carry the
    /// queryable DCB index keys (<c>sessionId</c>/<c>storyId</c>/<c>juniorId</c>/
    /// <c>level</c>/<c>blockerType</c>/<c>severity</c>/<c>tenantId</c>); <c>Data</c>
    /// carries the per-attempt / terminal payload. Status is driven off the event type
    /// (<see cref="BlockerEvents.StatusForEvent"/>) so a never-answered escalation
    /// (<c>BLOCKER.TIMED_OUT</c>) is a LOUD error row, never a false success. Pure (no
    /// Elsa context); exposed for unit testing the mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string? sessionId,
        string? storyId,
        string? juniorId,
        Guid? tenantId,
        string? blockerType,
        string? severity,
        string? level,
        int attempt,
        double confidence,
        string? progressType,
        double resolutionTimeSeconds)
    {
        var tags = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(sessionId)) tags["sessionId"] = sessionId;
        if (!string.IsNullOrWhiteSpace(storyId)) tags["storyId"] = storyId;
        if (!string.IsNullOrWhiteSpace(juniorId)) tags["juniorId"] = juniorId;
        if (!string.IsNullOrWhiteSpace(level)) tags["level"] = level;
        if (!string.IsNullOrWhiteSpace(blockerType)) tags["blockerType"] = blockerType;
        if (!string.IsNullOrWhiteSpace(severity)) tags["severity"] = severity;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>();
        if (attempt > 0) data["attempt"] = attempt;
        if (confidence > 0d) data["confidence"] = confidence;
        if (!string.IsNullOrWhiteSpace(progressType)) data["progressType"] = progressType;
        if (resolutionTimeSeconds > 0d) data["resolutionTimeSeconds"] = resolutionTimeSeconds;

        return new TammaEvent
        {
            EventType = type,
            Status = BlockerEvents.StatusForEvent(type),
            Tags = tags,
            Data = data,
        };
    }
}
