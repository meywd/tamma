using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Emits a <c>TRIAGE.PANEL.*</c> DCB event (Story 26-1 AC9 intent / triage-cluster
/// audit P1) for the audit trail by appending a <see cref="TammaEvent"/> to the
/// workflow's <c>tamma:events</c> transient list via
/// <see cref="TammaEventEmitter.Emit"/>. The engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that list
/// <i>durably</i> to the tenant <c>domain_events</c> store after this activity
/// runs — the drain resolves the tenant from the workflow scope (the triage panel
/// stamps a <c>TenantId</c> variable). The event therefore persists without this
/// activity holding any DB / repository dependency of its own (none is registered
/// in the Elsa engine — the same reason <see cref="EmitPrEventActivity"/> and
/// <see cref="EmitMergeApprovalEventActivity"/> use the emitter rather than a
/// direct <c>IEventRepository</c>).
///
/// <para>The panel emits <c>STARTED</c> right after init and exactly one terminal
/// event (<c>COMPLETED</c> / <c>PARTIAL</c> / <c>FAILED</c>) carrying the role
/// roster health (<c>roleCount</c> / <c>succeededCount</c> / <c>failedRoles</c>) —
/// so a degraded or failed panel is a loud audit row, never a silent false
/// success.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Emit Triage Panel Event",
    "Emit a TRIAGE.PANEL.* DCB event for the triage-panel audit trail",
    Kind = ActivityKind.Task
)]
public class EmitTriageEventActivity : Activity
{
    private readonly ILogger<EmitTriageEventActivity>? _logger;

    [Input(Description = "Event type — TRIAGE.PANEL.STARTED / .COMPLETED / .PARTIAL / .FAILED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Triage item number (0 when unknown)")]
    public Input<int> ItemNumber { get; set; } = new(0);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Total number of panel roles")]
    public Input<int> RoleCount { get; set; } = new(0);

    [Input(Description = "Number of roles that produced a usable assessment")]
    public Input<int> SucceededCount { get; set; } = new(0);

    [Input(Description = "JSON array of role names that failed to produce a usable assessment")]
    public Input<string?> FailedRolesJson { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitTriageEventActivity() { }

    public EmitTriageEventActivity(ILogger<EmitTriageEventActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.GetOrDefault(context) ?? TriageEvents.PanelFailed;
        var repository = Repository.GetOrDefault(context) ?? "";
        var itemNumber = ItemNumber.GetOrDefault(context);
        var tenantId = TriageEvents.ParseTenantId(TenantId.GetOrDefault(context));
        var roleCount = RoleCount.GetOrDefault(context);
        var succeededCount = SucceededCount.GetOrDefault(context);
        var failedRolesJson = FailedRolesJson.GetOrDefault(context);

        var evt = BuildTammaEvent(type, repository, itemNumber, tenantId, roleCount, succeededCount, failedRolesJson);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for item #{Item} in {Repo} (roles={Roles}, succeeded={Succeeded})",
            type, itemNumber, repository, roleCount, succeededCount);

        await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete (see EmitEscalationEventActivity precedent); without this the workflow hangs here forever
        return;
    }

    /// <summary>
    /// Map the panel inputs onto a <see cref="TammaEvent"/> expressed as the
    /// engine's transient-list event so the merged drain persists it. Tags carry
    /// the queryable DCB index keys (<c>repository</c>/<c>itemId</c>/
    /// <c>itemNumber</c>/<c>tenantId</c>); Data carries the panel-health payload
    /// (<c>roleCount</c>/<c>succeededCount</c>/<c>failedRoles</c>). Status is
    /// driven off the event type (failed → error, partial → warning) so a
    /// degraded/failed panel is never recorded as a false success. Pure (no Elsa
    /// context) — exposed for unit testing the mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string repository,
        int itemNumber,
        Guid? tenantId,
        int roleCount,
        int succeededCount,
        string? failedRolesJson)
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
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>
        {
            ["roleCount"] = roleCount,
            ["succeededCount"] = succeededCount,
            ["failedRoles"] = ParseFailedRoles(failedRolesJson),
        };

        return new TammaEvent
        {
            EventType = type,
            Status = TriageEvents.StatusForEvent(type),
            Tags = tags,
            Data = data,
        };
    }

    /// <summary>
    /// Deserialize the failed-roles JSON array into a list of role names for the
    /// event data. Returns an empty list on null / malformed input (the event
    /// still emits — an absent roster reads as "no recorded failures", never an
    /// exception). Exposed for unit testing the parse path.
    /// </summary>
    public static List<string> ParseFailedRoles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
