using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Emits a <c>MERGE.*</c> / <c>ISSUE.CLOSED.*</c> / <c>BRANCH.DELETED.*</c> DCB
/// event (Story 2-10 AC5 / Story 4.5) for the built-out <c>merge</c> workflow by
/// appending a <see cref="TammaEvent"/> to the workflow's <c>tamma:events</c>
/// transient list via <see cref="TammaEventEmitter.Emit"/>. The merged engine
/// event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes
/// that list <i>durably</i> to the tenant <c>domain_events</c> store after this
/// activity runs — the drain resolves the tenant from the workflow scope (this
/// workflow stamps a <c>TenantId</c> variable). The event therefore persists
/// without this activity holding any DB / repository dependency of its own (none
/// is registered in the Elsa engine — a directly-injected <c>IEventRepository</c>
/// would be inert and silently drop the event, the same trap the PR / branch
/// exemplars avoid).
///
/// <para>Critically, the <c>MERGE.FAILED</c> event ACTUALLY fires on a real merge
/// failure (the activity surfaces an <c>Error</c> outcome that the workflow routes
/// to a failure terminal which emits it), closing the headline bug where the thin
/// wrapper emitted NO event at all and dead-ended the flow on the unwired
/// <c>Error</c> outcome.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Emit Merge Event",
    "Emit a MERGE.SUCCESS / MERGE.FAILED / ISSUE.CLOSED.* / BRANCH.DELETED.* DCB event for the audit trail",
    Kind = ActivityKind.Task
)]
public class EmitMergeEventActivity : Activity
{
    private readonly ILogger<EmitMergeEventActivity>? _logger;

    [Input(Description = "Event type — MERGE.SUCCESS / MERGE.FAILED / ISSUE.CLOSED.* / BRANCH.DELETED.*")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Issue number this merge resolves")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Pull request number being merged")]
    public Input<int> PrNumber { get; set; } = default!;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Event data payload as JSON (mergeSha, mergeStrategy, issueClosed, branchDeleted, partial, failureCode, failureReason, durationMs)")]
    public Input<string?> DataJson { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitMergeEventActivity() { }

    public EmitMergeEventActivity(ILogger<EmitMergeEventActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.GetOrDefault(context) ?? MergeEvents.Failed;
        var issueNumber = IssueNumber.GetOrDefault(context);
        var prNumber = PrNumber.GetOrDefault(context);
        var repository = Repository.GetOrDefault(context) ?? "";
        var tenantId = MergeEvents.ParseTenantId(TenantId.GetOrDefault(context));
        var data = ParseData(DataJson.GetOrDefault(context));

        var evt = BuildTammaEvent(type, issueNumber, prNumber, repository, tenantId, data);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for PR #{Pr} / issue #{Issue} in {Repo}",
            type, prNumber, issueNumber, repository);

        await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete (see EmitEscalationEventActivity precedent); without this the workflow hangs here forever
        return;
    }

    /// <summary>
    /// Map the merge event inputs onto a <see cref="TammaEvent"/> expressed as the
    /// engine's transient-list event so the merged drain persists it. Tags carry
    /// the queryable DCB index keys (<c>issueId</c>/<c>issueNumber</c>/
    /// <c>prNumber</c>/<c>repository</c>/<c>tenantId</c>); <c>Data</c> carries the
    /// operation payload (mergeSha / strategy / sub-action results). Pure (no Elsa
    /// context); exposed for testing.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        int issueNumber,
        int prNumber,
        string repository,
        Guid? tenantId,
        IReadOnlyDictionary<string, object?>? data)
    {
        var tags = new Dictionary<string, object?>
        {
            ["issueId"] = issueNumber.ToString(),
            ["issueNumber"] = issueNumber.ToString(),
            ["prNumber"] = prNumber.ToString(),
            ["prId"] = prNumber.ToString(),
            ["repository"] = repository,
        };
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        return new TammaEvent
        {
            EventType = type,
            Status = IsFailureType(type) ? "error" : "success",
            Tags = tags,
            Data = data is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(data),
        };
    }

    /// <summary>
    /// A <c>*.FAILED</c> event type carries an <c>error</c> status; everything else
    /// (<c>*.SUCCESS</c> / <c>*.CHECKED</c>) is <c>success</c>.
    /// </summary>
    public static bool IsFailureType(string type)
        => type.EndsWith(".FAILED", StringComparison.Ordinal);

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
