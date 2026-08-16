using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Emits an <c>ISSUE_STATUS.*</c> DCB event (Epic 4 audit trail) for the
/// built-out <c>update-issue-status</c> workflow by appending a
/// <see cref="TammaEvent"/> to the workflow's <c>tamma:events</c> transient list
/// via <see cref="TammaEventEmitter.Emit"/>. The merged engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that list
/// <i>durably</i> to the tenant <c>domain_events</c> store after this activity
/// runs — the drain resolves the tenant from the workflow scope (this workflow
/// stamps a <c>TenantId</c> variable). The event therefore persists without this
/// activity holding any DB / repository dependency of its own (none is registered
/// in the Elsa engine — a directly-injected <c>IEventRepository</c> would be
/// inert and silently drop the event, the same trap the PR exemplar avoids).
///
/// <para>Critically, the FAILED event ACTUALLY fires on a real callback failure
/// (the activity surfaces a <c>Failed</c> outcome that the workflow routes here),
/// closing the headline swallow-failure bug where a failed update was recorded as
/// <c>.COMPLETED</c>.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Emit Issue Status Event",
    "Emit an ISSUE_STATUS.UPDATED.SUCCESS / .FAILED DCB event for the audit trail",
    Kind = ActivityKind.Task
)]
public class EmitIssueStatusEventActivity : Activity
{
    private readonly ILogger<EmitIssueStatusEventActivity>? _logger;

    [Input(Description = "Event type — ISSUE_STATUS.UPDATED.SUCCESS or .FAILED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Event data payload as JSON (message, addLabels, removeLabels, degraded, error, errorCode, durationMs)")]
    public Input<string?> DataJson { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitIssueStatusEventActivity() { }

    public EmitIssueStatusEventActivity(ILogger<EmitIssueStatusEventActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.GetOrDefault(context) ?? IssueStatusEvents.UpdatedFailed;
        var issueNumber = IssueNumber.GetOrDefault(context);
        var repository = Repository.GetOrDefault(context) ?? "";
        var tenantId = IssueStatusEvents.ParseTenantId(TenantId.GetOrDefault(context));
        var data = ParseData(DataJson.GetOrDefault(context));

        var evt = BuildTammaEvent(type, issueNumber, repository, tenantId, data);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for issue #{Issue} in {Repo}", type, issueNumber, repository);

        await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete (see EmitEscalationEventActivity precedent); without this the workflow hangs here forever
        return;
    }

    /// <summary>
    /// Map the issue-status event inputs onto a <see cref="TammaEvent"/> expressed
    /// as the engine's transient-list event so the merged drain persists it. Tags
    /// carry the queryable DCB index keys (<c>issueId</c>/<c>issueNumber</c>/
    /// <c>repository</c>/<c>tenantId</c>); <c>Data</c> carries the operation
    /// payload (key-free — no token). Pure (no Elsa context); exposed for testing.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        int issueNumber,
        string repository,
        Guid? tenantId,
        IReadOnlyDictionary<string, object?>? data)
    {
        var tags = new Dictionary<string, object?>
        {
            ["issueId"] = issueNumber.ToString(),
            ["issueNumber"] = issueNumber.ToString(),
            ["repository"] = repository,
        };
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        // A degraded (callback-unavailable) no-op still emits a SUCCESS event. Lift
        // the degraded flag from Data into a QUERYABLE tag so a consumer filtering
        // on event TYPE can exclude these no-ops — otherwise a degrade reads as a
        // genuine success (the Data-only flag is non-indexed). Kept in Data too.
        if (IsDegraded(data)) tags["degraded"] = "true";

        return new TammaEvent
        {
            EventType = type,
            Status = type == IssueStatusEvents.UpdatedFailed ? "error" : "success",
            Tags = tags,
            Data = data is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(data),
        };
    }

    /// <summary>
    /// True when the event payload flags a degraded no-op. Tolerates both a direct
    /// <see cref="bool"/> (in-process dictionary) and a <see cref="JsonElement"/>
    /// (the payload arrives via <see cref="ParseData"/> in the running workflow).
    /// </summary>
    private static bool IsDegraded(IReadOnlyDictionary<string, object?>? data)
    {
        if (data is null || !data.TryGetValue("degraded", out var raw) || raw is null)
            return false;
        return raw switch
        {
            bool b => b,
            System.Text.Json.JsonElement je => je.ValueKind == System.Text.Json.JsonValueKind.True
                || (je.ValueKind == System.Text.Json.JsonValueKind.String
                    && bool.TryParse(je.GetString(), out var pb) && pb),
            string s => bool.TryParse(s, out var sb) && sb,
            _ => false,
        };
    }

    /// <summary>
    /// Deserialize the JSON data payload into a dictionary for the event builder.
    /// Returns null on empty/malformed input (the event still emits with "{}").
    /// Exposed for unit testing the parse path.
    /// </summary>
    public static IReadOnlyDictionary<string, object?>? ParseData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, object?>>(json);
        }
        catch
        {
            return null;
        }
    }
}
