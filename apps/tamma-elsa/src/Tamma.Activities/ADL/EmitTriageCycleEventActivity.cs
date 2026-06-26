using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Emits a <b>cycle-scoped</b> <c>TRIAGE.ISSUE.*</c> DCB event (completeness audit
/// 2026-06-22, <c>TriageItemCycle.md</c> #3 / Story 26-1 AC9) for the audit trail by
/// appending a <see cref="TammaEvent"/> to the workflow's <c>tamma:events</c> transient
/// list via <see cref="TammaEventEmitter.Emit"/>. The engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that list
/// <i>durably</i> to the tenant <c>domain_events</c> store after this activity runs —
/// the drain resolves the tenant from the workflow scope (the <c>triage-item-cycle</c>
/// workflow stamps a <c>TenantId</c> variable). The event therefore persists without
/// this activity holding any DB / repository dependency of its own (none is registered
/// in the Elsa engine — the same reason <see cref="EmitTriageContextEventActivity"/>,
/// <see cref="EmitTriageEventActivity"/> and <see cref="EmitTriagePoDecisionEventActivity"/>
/// use the emitter rather than a direct <c>IEventRepository</c>).
///
/// <para>The cycle emits <c>STARTED</c> right at init and exactly one terminal event:
/// <c>COMPLETED</c> (labels/comment applied), <c>SKIPPED</c> (a stage reported a
/// non-applying-but-not-faulted signal — context unavailable / panel below quorum), or
/// <c>FAILED</c> (a sub-workflow faulted, the PO produced no usable decision, or the
/// apply failed). A skipped or failed cycle is a loud audit row, never a silent false
/// success.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Emit Triage Cycle Event",
    "Emit a cycle-scoped TRIAGE.ISSUE.* DCB event for the per-item triage audit trail",
    Kind = ActivityKind.Task
)]
public class EmitTriageCycleEventActivity : Activity
{
    private readonly ILogger<EmitTriageCycleEventActivity>? _logger;

    [Input(Description = "Event type — TRIAGE.ISSUE.STARTED / .COMPLETED / .SKIPPED / .FAILED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Deterministic item key — repo#number for an issue, repo:source for an alert")]
    public Input<string?> ItemKey { get; set; } = new((string?)null);

    [Input(Description = "Triage item number (0 when unknown / an alert)")]
    public Input<int> ItemNumber { get; set; } = new(0);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Item source — issue / dependabot / codeql / ... (from the item JSON)")]
    public Input<string?> ItemSource { get; set; } = new((string?)null);

    [Input(Description = "Decided type (empty unless COMPLETED)")]
    public Input<string?> Type { get; set; } = new((string?)null);

    [Input(Description = "Decided priority (empty unless COMPLETED)")]
    public Input<string?> Priority { get; set; } = new((string?)null);

    [Input(Description = "Decided automation level (empty unless COMPLETED)")]
    public Input<string?> Automation { get; set; } = new((string?)null);

    [Input(Description = "Decision status carried from the PO step (ok / unparsed / llm-failed / skipped)")]
    public Input<string?> DecisionStatus { get; set; } = new((string?)null);

    [Input(Description = "Short, secret-free reason/error (empty unless SKIPPED / FAILED)")]
    public Input<string?> Reason { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitTriageCycleEventActivity() { }

    public EmitTriageCycleEventActivity(ILogger<EmitTriageCycleEventActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.Get(context) ?? TriageCycleEvents.Failed;
        var repository = Repository.Get(context) ?? "";
        var itemKey = ItemKey.Get(context);
        var itemNumber = ItemNumber.Get(context);
        var tenantId = TriageCycleEvents.ParseTenantId(TenantId.Get(context));
        var itemSource = ItemSource.Get(context);
        var type2 = Type.Get(context);
        var priority = Priority.Get(context);
        var automation = Automation.Get(context);
        var decisionStatus = DecisionStatus.Get(context);
        var reason = Reason.Get(context);

        var evt = BuildTammaEvent(
            type, repository, itemKey, itemNumber, tenantId, itemSource,
            type2, priority, automation, decisionStatus, reason);

        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for {ItemKey} in {Repo} (source={Source}, status={Status})",
            type, itemKey, repository, itemSource, decisionStatus);

        return default;
    }

    /// <summary>
    /// Map the cycle inputs onto a <see cref="TammaEvent"/> expressed as the engine's
    /// transient-list event so the merged drain persists it. Tags carry the queryable
    /// DCB index keys requested by the build-out spec
    /// (<c>repository</c>/<c>itemKey</c>/<c>issueId</c>/<c>itemNumber</c>/
    /// <c>itemSource</c>/<c>type</c>/<c>priority</c>/<c>automation</c>/<c>tenantId</c>);
    /// Data carries the cycle payload (<c>itemSource</c>/<c>type</c>/<c>priority</c>/
    /// <c>automation</c>/<c>decisionStatus</c>). Status is driven off the event type
    /// (failed → error, skipped → warning) so a skipped/failed cycle is never recorded
    /// as a false success. Pure (no Elsa context) — exposed for unit testing the mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string repository,
        string? itemKey,
        int itemNumber,
        Guid? tenantId,
        string? itemSource,
        string? itemType,
        string? priority,
        string? automation,
        string? decisionStatus,
        string? reason)
    {
        var tags = new Dictionary<string, object?>
        {
            ["repository"] = repository,
        };
        if (!string.IsNullOrWhiteSpace(itemKey)) tags["itemKey"] = itemKey;
        if (itemNumber > 0)
        {
            tags["itemId"] = itemNumber.ToString();
            tags["itemNumber"] = itemNumber.ToString();
            tags["issueId"] = itemNumber.ToString();
        }
        if (!string.IsNullOrWhiteSpace(itemSource)) tags["itemSource"] = itemSource;
        if (!string.IsNullOrWhiteSpace(itemType)) tags["type"] = itemType;
        if (!string.IsNullOrWhiteSpace(priority)) tags["priority"] = priority;
        if (!string.IsNullOrWhiteSpace(automation)) tags["automation"] = automation;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>
        {
            ["itemSource"] = itemSource ?? "",
            ["type"] = itemType ?? "",
            ["priority"] = priority ?? "",
            ["automation"] = automation ?? "",
            ["decisionStatus"] = decisionStatus ?? "",
        };

        return new TammaEvent
        {
            EventType = type,
            Status = TriageCycleEvents.StatusForEvent(type),
            Error = type == TriageCycleEvents.Failed && !string.IsNullOrWhiteSpace(reason)
                ? reason
                : null,
            Tags = tags,
            Data = data,
        };
    }
}
