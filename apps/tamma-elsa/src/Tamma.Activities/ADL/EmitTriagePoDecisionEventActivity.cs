using System.Globalization;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Emits a <c>TRIAGE.PO_DECISION.*</c> DCB event (completeness audit 2026-06-22,
/// <c>TriagePODecision.md</c> #3 / Story 26-1 AC) for the audit trail by appending
/// a <see cref="TammaEvent"/> to the workflow's <c>tamma:events</c> transient list
/// via <see cref="TammaEventEmitter.Emit"/>. The engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that list
/// <i>durably</i> to the tenant <c>domain_events</c> store after this activity runs.
/// The event therefore persists without this activity holding any DB / repository
/// dependency of its own (none is registered in the Elsa engine — the same reason
/// <see cref="EmitTriageEventActivity"/> and <see cref="EmitTriageContextEventActivity"/>
/// use the emitter rather than a direct <c>IEventRepository</c>).
///
/// <para>The PO step emits <c>STARTED</c> right after init and exactly one terminal
/// event: <c>COMPLETED</c> (decision produced — data carries the classification +
/// provider/cost), <c>FAILED</c> (the <c>llm-call</c> reported failure — loud,
/// error-status, NO fabricated decision), or <c>SKIPPED</c> (empty input). A failed
/// or skipped PO step is a loud audit row, never a silent false success.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Emit Triage PO Decision Event",
    "Emit a TRIAGE.PO_DECISION.* DCB event for the triage PO-decision audit trail",
    Kind = ActivityKind.Task
)]
public class EmitTriagePoDecisionEventActivity : Activity
{
    private readonly ILogger<EmitTriagePoDecisionEventActivity>? _logger;

    [Input(Description = "Event type — TRIAGE.PO_DECISION.STARTED / .COMPLETED / .FAILED / .SKIPPED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Triage item number (0 when unknown)")]
    public Input<int> ItemNumber { get; set; } = new(0);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Decision status — ok / unparsed / llm-failed / skipped (empty on STARTED)")]
    public Input<string?> DecisionStatus { get; set; } = new((string?)null);

    [Input(Description = "Decided priority (empty on STARTED / FAILED / SKIPPED)")]
    public Input<string?> Priority { get; set; } = new((string?)null);

    [Input(Description = "Decided type (empty on STARTED / FAILED / SKIPPED)")]
    public Input<string?> Type { get; set; } = new((string?)null);

    [Input(Description = "Decided complexity (empty on STARTED / FAILED / SKIPPED)")]
    public Input<string?> Complexity { get; set; } = new((string?)null);

    [Input(Description = "Decided automation level (empty on STARTED / FAILED / SKIPPED)")]
    public Input<string?> Automation { get; set; } = new((string?)null);

    [Input(Description = "Provider that produced the decision (empty when no call ran)")]
    public Input<string?> ProviderUsed { get; set; } = new((string?)null);

    [Input(Description = "Cost in USD of the llm-call (0 when no call ran)")]
    public Input<decimal> CostUsd { get; set; } = new(0m);

    [Input(Description = "Short, secret-free failure summary (empty unless FAILED)")]
    public Input<string?> Error { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitTriagePoDecisionEventActivity() { }

    public EmitTriagePoDecisionEventActivity(ILogger<EmitTriagePoDecisionEventActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.Get(context) ?? TriagePoDecisionEvents.Failed;
        var repository = Repository.Get(context) ?? "";
        var itemNumber = ItemNumber.Get(context);
        var tenantId = TriagePoDecisionEvents.ParseTenantId(TenantId.Get(context));

        var evt = BuildTammaEvent(
            type, repository, itemNumber, tenantId,
            DecisionStatus.Get(context), Priority.Get(context), Type.Get(context),
            Complexity.Get(context), Automation.Get(context),
            ProviderUsed.Get(context), CostUsd.Get(context), Error.Get(context));

        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for item #{Item} in {Repo} (status={Status}, provider={Provider})",
            type, itemNumber, repository, DecisionStatus.Get(context), ProviderUsed.Get(context));

        return default;
    }

    /// <summary>
    /// Map the PO-decision inputs onto a <see cref="TammaEvent"/> expressed as the
    /// engine's transient-list event so the merged drain persists it. Tags carry
    /// the queryable DCB index keys (<c>repository</c>/<c>itemId</c>/<c>itemNumber</c>/
    /// <c>issueId</c>/<c>provider</c>/<c>tenantId</c>); Data carries the decision
    /// payload (<c>decisionStatus</c>/<c>priority</c>/<c>type</c>/<c>complexity</c>/
    /// <c>automation</c>/<c>providerUsed</c>/<c>costUsd</c>). Status is driven off the
    /// event type (failed → error, skipped → warning) so a failed/skipped PO step is
    /// never recorded as a false success. Pure (no Elsa context) — exposed for unit
    /// testing the mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string repository,
        int itemNumber,
        Guid? tenantId,
        string? decisionStatus,
        string? priority,
        string? itemType,
        string? complexity,
        string? automation,
        string? providerUsed,
        decimal costUsd,
        string? error)
    {
        var tags = new Dictionary<string, object?>
        {
            ["repository"] = repository,
        };
        if (itemNumber > 0)
        {
            tags["itemId"] = itemNumber.ToString();
            tags["itemNumber"] = itemNumber.ToString();
            // issueId tag per the DCB tag convention (the build-out spec asks to
            // "tag with issueId from the item").
            tags["issueId"] = itemNumber.ToString();
        }
        if (!string.IsNullOrWhiteSpace(providerUsed)) tags["provider"] = providerUsed;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>
        {
            ["decisionStatus"] = decisionStatus ?? "",
            ["priority"] = priority ?? "",
            ["type"] = itemType ?? "",
            ["complexity"] = complexity ?? "",
            ["automation"] = automation ?? "",
            ["providerUsed"] = providerUsed ?? "",
            ["costUsd"] = costUsd,
        };

        return new TammaEvent
        {
            EventType = type,
            Status = TriagePoDecisionEvents.StatusForEvent(type),
            Error = string.IsNullOrWhiteSpace(error) ? null : error,
            Tags = tags,
            Data = data,
        };
    }

    /// <summary>
    /// Tolerant parse of the loose <c>costUsd</c> value the <c>llm-call</c> result
    /// dictionary carries (a boxed <c>decimal</c>, a <c>double</c>, or a string).
    /// Returns 0 on null / unparseable input. Exposed for unit testing.
    /// </summary>
    public static decimal ParseCost(object? raw)
    {
        switch (raw)
        {
            case null: return 0m;
            case decimal d: return d;
            case double db: return (decimal)db;
            case float f: return (decimal)f;
            case int i: return i;
            case long l: return l;
            default:
                return decimal.TryParse(
                    raw.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed : 0m;
        }
    }
}
