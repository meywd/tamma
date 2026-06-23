using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Emits a <c>TRIAGE.CONTEXT.*</c> DCB event (completeness audit 2026-06-22,
/// <c>TriageContextGathering.md</c> §5 #4) for the audit trail by appending a
/// <see cref="TammaEvent"/> to the workflow's <c>tamma:events</c> transient list via
/// <see cref="TammaEventEmitter.Emit"/>. The engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that list
/// <i>durably</i> to the tenant <c>domain_events</c> store after this activity runs
/// — the drain resolves the tenant from the workflow scope (the context-gathering
/// workflow stamps a <c>TenantId</c> variable). The event therefore persists without
/// this activity holding any DB / repository dependency of its own (none is
/// registered in the Elsa engine — the same reason <see cref="EmitTriageEventActivity"/>,
/// <see cref="EmitPrEventActivity"/> and <see cref="EmitMergeApprovalEventActivity"/>
/// use the emitter rather than a direct <c>IEventRepository</c>).
///
/// <para>The stage emits <c>STARTED</c> right after init and exactly one terminal
/// event (<c>COMPLETED</c> / <c>EMPTY</c> / <c>FAILED</c>) carrying the gathered
/// context health (<c>contextStatus</c> / <c>contextJsonLength</c> / <c>itemType</c>)
/// — so a degraded (empty) or failed scan is a loud audit row, never a silent false
/// success.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Emit Triage Context Event",
    "Emit a TRIAGE.CONTEXT.* DCB event for the triage-context audit trail",
    Kind = ActivityKind.Task
)]
public class EmitTriageContextEventActivity : Activity
{
    private readonly ILogger<EmitTriageContextEventActivity>? _logger;

    [Input(Description = "Event type — TRIAGE.CONTEXT.STARTED / .COMPLETED / .EMPTY / .FAILED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Triage item number (0 when unknown)")]
    public Input<int> ItemNumber { get; set; } = new(0);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Detected item type — issue / security / dependency")]
    public Input<string?> ItemType { get; set; } = new((string?)null);

    [Input(Description = "Terminal context status — ok / empty / failed (empty on STARTED)")]
    public Input<string?> ContextStatus { get; set; } = new((string?)null);

    [Input(Description = "Length of the gathered context JSON (0 on STARTED / FAILED)")]
    public Input<int> ContextJsonLength { get; set; } = new(0);

    [JsonConstructor]
    public EmitTriageContextEventActivity() { }

    public EmitTriageContextEventActivity(ILogger<EmitTriageContextEventActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.Get(context) ?? TriageContextEvents.Failed;
        var repository = Repository.Get(context) ?? "";
        var itemNumber = ItemNumber.Get(context);
        var tenantId = TriageContextEvents.ParseTenantId(TenantId.Get(context));
        var itemType = ItemType.Get(context);
        var contextStatus = ContextStatus.Get(context);
        var contextJsonLength = ContextJsonLength.Get(context);

        var evt = BuildTammaEvent(type, repository, itemNumber, tenantId, itemType, contextStatus, contextJsonLength);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for item #{Item} in {Repo} (itemType={ItemType}, status={Status}, len={Len})",
            type, itemNumber, repository, itemType, contextStatus, contextJsonLength);

        return default;
    }

    /// <summary>
    /// Map the context inputs onto a <see cref="TammaEvent"/> expressed as the
    /// engine's transient-list event so the merged drain persists it. Tags carry
    /// the queryable DCB index keys (<c>repository</c>/<c>itemId</c>/
    /// <c>itemNumber</c>/<c>itemSource</c>/<c>contextStatus</c>/<c>tenantId</c>);
    /// Data carries the context-health payload (<c>itemType</c>/<c>contextStatus</c>/
    /// <c>contextJsonLength</c>). Status is driven off the event type (failed →
    /// error, empty → warning) so a degraded/failed scan is never recorded as a
    /// false success. Pure (no Elsa context) — exposed for unit testing the mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string repository,
        int itemNumber,
        Guid? tenantId,
        string? itemType,
        string? contextStatus,
        int contextJsonLength)
    {
        var tags = new Dictionary<string, object?>
        {
            ["repository"] = repository,
        };
        if (itemNumber > 0)
        {
            tags["itemId"] = itemNumber.ToString();
            tags["itemNumber"] = itemNumber.ToString();
        }
        if (!string.IsNullOrWhiteSpace(itemType)) tags["itemSource"] = itemType;
        if (!string.IsNullOrWhiteSpace(contextStatus)) tags["contextStatus"] = contextStatus;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>
        {
            ["itemType"] = itemType ?? "",
            ["contextStatus"] = contextStatus ?? "",
            ["contextJsonLength"] = contextJsonLength,
        };

        return new TammaEvent
        {
            EventType = type,
            Status = TriageContextEvents.StatusForEvent(type),
            Tags = tags,
            Data = data,
        };
    }
}
