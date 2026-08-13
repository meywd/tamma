using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Emits a <c>TDD_DEBUG.*</c> DCB event (completeness audit 2026-06-22,
/// <c>TddWithDebugRetry.md</c> §Missing #3) for the built-out
/// <c>tdd-with-debug-retry</c> orchestrator by appending a <see cref="TammaEvent"/>
/// to the workflow's <c>tamma:events</c> transient list via
/// <see cref="TammaEventEmitter.Emit"/>. The merged engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that list
/// <i>durably</i> to the tenant <c>domain_events</c> store after this activity runs
/// — the drain resolves the tenant from the workflow scope (the orchestrator stamps
/// a <c>TenantId</c> variable). The event therefore persists without this activity
/// holding any DB / repository dependency of its own (none is registered in the Elsa
/// engine — the same reason <see cref="EmitBranchEventActivity"/>,
/// <see cref="EmitPrEventActivity"/> and <see cref="EmitTriageContextEventActivity"/>
/// use the emitter rather than a direct <c>IEventRepository</c>).
///
/// <para>The orchestrator emits a <c>STARTED</c> before each cycle dispatch and a
/// loud terminal (<c>RETRY.EXHAUSTED</c> / <c>DEBUGGER.ESCALATED</c> are error-status)
/// so an exhausted retry loop or a debugger escalation is a LOUD audit row, never a
/// silent false success.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Emit TDD Debug Event",
    "Emit a TDD_DEBUG.* DCB event for the TDD-with-debug-retry audit trail",
    Kind = ActivityKind.Task
)]
public class EmitTddDebugEventActivity : Activity
{
    private readonly ILogger<EmitTddDebugEventActivity>? _logger;

    [Input(Description = "Event type — TDD_DEBUG.CYCLE.STARTED / .PASSED / .FAILED / .DEBUG.ATTEMPTED / .DEBUGGER.ESCALATED / .RETRY.EXHAUSTED / .COMPLETED.SUCCESS")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Story id this TDD cycle implements")]
    public Input<string?> StoryId { get; set; } = new((string?)null);

    [Input(Description = "Issue number driving the cycle (0 when unknown)")]
    public Input<int> IssueNumber { get; set; } = new(0);

    [Input(Description = "Repository in owner/repo format")]
    public Input<string?> Repository { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Current debug-retry attempt number (0 before the first retry)")]
    public Input<int> Attempt { get; set; } = new(0);

    [Input(Description = "Max debug-retry budget for the loop")]
    public Input<int> MaxRetries { get; set; } = new(0);

    [Input(Description = "Terminal failure reason (tdd-not-converged / debugger-escalated; empty on non-terminal events)")]
    public Input<string?> FinishReason { get; set; } = new((string?)null);

    [Input(Description = "Underlying TDD failure detail surfaced on a terminal failure (empty otherwise; never the raw planJson)")]
    public Input<string?> ErrorDetail { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitTddDebugEventActivity() { }

    public EmitTddDebugEventActivity(ILogger<EmitTddDebugEventActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.GetOrDefault(context) ?? TddDebugEvents.RetryExhausted;
        var storyId = StoryId.GetOrDefault(context);
        var issueNumber = IssueNumber.GetOrDefault(context);
        var repository = Repository.GetOrDefault(context);
        var tenantId = TddDebugEvents.ParseTenantId(TenantId.GetOrDefault(context));
        var attempt = Attempt.GetOrDefault(context);
        var maxRetries = MaxRetries.GetOrDefault(context);
        var finishReason = FinishReason.GetOrDefault(context);
        var errorDetail = ErrorDetail.GetOrDefault(context);

        var evt = BuildTammaEvent(type, storyId, issueNumber, repository, tenantId, attempt, maxRetries, finishReason, errorDetail);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for story {Story} issue #{Issue} (attempt={Attempt}/{Max}, reason={Reason})",
            type, storyId, issueNumber, attempt, maxRetries, finishReason);

        await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete (see EmitEscalationEventActivity precedent); without this the workflow hangs here forever
        return;
    }

    /// <summary>
    /// Map the TDD-debug inputs onto a <see cref="TammaEvent"/> expressed as the
    /// engine's transient-list event so the merged drain persists it. Tags carry the
    /// queryable DCB index keys (<c>storyId</c>/<c>issueId</c>/<c>issueNumber</c>/
    /// <c>repository</c>/<c>attempt</c>/<c>tenantId</c>); <c>Data</c> carries the loop
    /// payload (<c>attempt</c>/<c>maxRetries</c>/<c>finishReason</c>/<c>errorDetail</c>).
    /// Status is driven off the event type (retry-exhausted / debugger-escalated →
    /// error) so a degraded/failed terminal is never recorded as a false success.
    /// Pure (no Elsa context); exposed for unit testing the mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string? storyId,
        int issueNumber,
        string? repository,
        Guid? tenantId,
        int attempt,
        int maxRetries,
        string? finishReason,
        string? errorDetail)
    {
        var tags = new Dictionary<string, object?>
        {
            ["attempt"] = attempt.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(storyId)) tags["storyId"] = storyId;
        if (issueNumber > 0)
        {
            tags["issueId"] = issueNumber.ToString();
            tags["issueNumber"] = issueNumber.ToString();
        }
        if (!string.IsNullOrWhiteSpace(repository)) tags["repository"] = repository;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>
        {
            ["attempt"] = attempt,
            ["maxRetries"] = maxRetries,
        };
        if (!string.IsNullOrWhiteSpace(finishReason)) data["finishReason"] = finishReason;
        if (!string.IsNullOrWhiteSpace(errorDetail)) data["errorDetail"] = errorDetail;

        return new TammaEvent
        {
            EventType = type,
            Status = TddDebugEvents.StatusForEvent(type),
            Tags = tags,
            Data = data,
        };
    }
}
