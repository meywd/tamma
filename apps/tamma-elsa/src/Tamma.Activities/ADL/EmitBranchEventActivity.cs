using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Emits a <c>BRANCH.*</c> DCB event (Story 2.4 AC8 / Story 4.5 AC3) for the
/// built-out <c>branch-creation</c> workflow by appending a <see cref="TammaEvent"/>
/// to the workflow's <c>tamma:events</c> transient list via
/// <see cref="TammaEventEmitter.Emit"/>. The merged engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that list
/// <i>durably</i> to the tenant <c>domain_events</c> store after this activity
/// runs — the drain resolves the tenant from the workflow scope (this workflow
/// stamps a <c>TenantId</c> variable). The event therefore persists without this
/// activity holding any DB / repository dependency of its own (none is registered
/// in the Elsa engine — a directly-injected <c>IEventRepository</c> would be inert
/// and silently drop the event, the same trap the PR / issue-status exemplars avoid).
///
/// <para>Critically, the FAILED event ACTUALLY fires on a real create failure (the
/// activity surfaces an <c>Error</c> outcome that the workflow routes here),
/// closing the headline bug where the thin wrapper emitted NO event at all and
/// reported a swallowed <c>success=false</c> with a dangling Error edge.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Emit Branch Event",
    "Emit a BRANCH.CREATED.SUCCESS / BRANCH.CREATED.FAILED DCB event for the audit trail",
    Kind = ActivityKind.Task
)]
public class EmitBranchEventActivity : Activity
{
    private readonly ILogger<EmitBranchEventActivity>? _logger;

    [Input(Description = "Event type — BRANCH.CREATED.SUCCESS or BRANCH.CREATED.FAILED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Issue number this branch isolates")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Event data payload as JSON (baseBranch, baseSha, finalName, conflictResolved, error, errorCode, durationMs)")]
    public Input<string?> DataJson { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitBranchEventActivity() { }

    public EmitBranchEventActivity(ILogger<EmitBranchEventActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.Get(context) ?? BranchEvents.CreatedFailed;
        var issueNumber = IssueNumber.Get(context);
        var repository = Repository.Get(context) ?? "";
        var tenantId = BranchEvents.ParseTenantId(TenantId.Get(context));
        var data = ParseData(DataJson.Get(context));

        var evt = BuildTammaEvent(type, issueNumber, repository, tenantId, data);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for issue #{Issue} in {Repo}", type, issueNumber, repository);

        return default;
    }

    /// <summary>
    /// Map the branch event inputs onto a <see cref="TammaEvent"/> expressed as the
    /// engine's transient-list event so the merged drain persists it. Tags carry the
    /// queryable DCB index keys (<c>issueId</c>/<c>issueNumber</c>/<c>repository</c>/
    /// <c>tenantId</c>); <c>Data</c> carries the operation payload (key-free — no
    /// token). Pure (no Elsa context); exposed for testing.
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

        return new TammaEvent
        {
            EventType = type,
            Status = type == BranchEvents.CreatedFailed ? "error" : "success",
            Tags = tags,
            Data = data is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(data),
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
