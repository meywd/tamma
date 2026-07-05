using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.Decomposition;

/// <summary>
/// Story 2.14 — emits a <c>DECOMPOSITION.*</c> DCB event for the <c>issue-decomposition</c>
/// sub-workflow by appending a <see cref="TammaEvent"/> to the workflow's <c>tamma:events</c>
/// transient list via <see cref="TammaEventEmitter.Emit"/>. The merged engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that list <i>durably</i> to the
/// tenant <c>domain_events</c> store — the same pattern
/// <see cref="Tamma.Activities.Research.EmitResearchEventActivity"/> and
/// <see cref="Tamma.Activities.Ambiguity.EmitAmbiguityEventActivity"/> use. No activity holds a
/// DB / repository dependency of its own (none is registered in the Elsa engine — a directly
/// injected repository would be inert and silently drop every event).
/// </summary>
[Activity(
    "Tamma.Decomposition",
    "Emit Decomposition Event",
    "Emit a DECOMPOSITION.* DCB event for the issue-decomposition workflow audit trail",
    Kind = ActivityKind.Task
)]
public class EmitDecompositionEventActivity : Activity
{
    private readonly ILogger<EmitDecompositionEventActivity>? _logger;

    [Input(Description = "Event type — DECOMPOSITION.STARTED / .CONTEXT_GATHERED / .COMPLETED / .FAILED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Decomposition session id (unguessable Guid string)")]
    public Input<string?> SessionId { get; set; } = new((string?)null);

    [Input(Description = "Issue / requirement id being decomposed")]
    public Input<string?> IssueId { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Number of sub-tasks produced (0 until completion / on failure)")]
    public Input<int> SubtaskCount { get; set; } = new(0);

    [Input(Description = "Free-text detail for the audit payload")]
    public Input<string?> Detail { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitDecompositionEventActivity() { }

    public EmitDecompositionEventActivity(ILogger<EmitDecompositionEventActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.Get(context) ?? DecompositionEvents.Failed;
        var sessionId = SessionId.Get(context);
        var issueId = IssueId.Get(context);
        var tenantId = DecompositionEvents.ParseTenantId(TenantId.Get(context));
        var subtaskCount = SubtaskCount.Get(context);
        var detail = Detail.Get(context);

        var evt = BuildTammaEvent(type, sessionId, issueId, tenantId, subtaskCount, detail);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for decomposition session {Session} issue {Issue} (subtasks={Count})",
            type, sessionId, issueId, subtaskCount);

        return default;
    }

    /// <summary>
    /// Map the decomposition event inputs onto a <see cref="TammaEvent"/> expressed as the engine's
    /// transient-list event so the merged drain persists it. Tags carry the queryable DCB index
    /// keys (<c>sessionId</c>/<c>issueId</c>/<c>tenantId</c>); <c>Data</c> carries the
    /// per-transition payload (<c>subtaskCount</c>/<c>detail</c>). Status is driven off the event
    /// type (<see cref="DecompositionEvents.StatusForEvent"/>) so a failed terminal is a LOUD error
    /// row, never a false success. Pure (no Elsa context); exposed for unit testing the mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string? sessionId,
        string? issueId,
        Guid? tenantId,
        int subtaskCount,
        string? detail)
    {
        var tags = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(sessionId)) tags["sessionId"] = sessionId;
        if (!string.IsNullOrWhiteSpace(issueId)) tags["issueId"] = issueId;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>();
        if (subtaskCount > 0) data["subtaskCount"] = subtaskCount;
        if (!string.IsNullOrWhiteSpace(detail)) data["detail"] = detail;

        return new TammaEvent
        {
            EventType = type,
            Status = DecompositionEvents.StatusForEvent(type),
            Tags = tags,
            Data = data,
        };
    }
}
