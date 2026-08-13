using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Emits a <c>REVIEW_FIX.*</c> DCB event (Story 2-18 Phases 4 &amp; 5 / Story 2-9)
/// for the audit trail by appending a <see cref="TammaEvent"/> to the workflow's
/// <c>tamma:events</c> transient list via <see cref="TammaEventEmitter.Emit"/>.
/// The engine event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>)
/// flushes that list <i>durably</i> to the tenant <c>domain_events</c> store after
/// this activity runs — the drain resolves the tenant from the workflow scope (the
/// review-fix workflow stamps a <c>TenantId</c> variable). The event therefore
/// persists without this activity holding any DB / repository dependency of its
/// own (none is registered in the Elsa engine — the same reason
/// <see cref="EmitPrEventActivity"/> / <see cref="EmitMergeApprovalEventActivity"/>
/// use the emitter rather than a direct <c>IEventRepository</c>).
/// </summary>
[Activity(
    "Tamma.ADL",
    "Emit Review Fix Event",
    "Emit a REVIEW_FIX.* DCB event for the review-fix audit trail",
    Kind = ActivityKind.Task
)]
public class EmitReviewFixEventActivity : Activity
{
    private readonly ILogger<EmitReviewFixEventActivity>? _logger;

    [Input(Description = "Event type — e.g. REVIEW_FIX.ANALYZED.SUCCESS / REVIEW_FIX.APPLIED.FAILED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string?> Repository { get; set; } = new((string?)null);

    [Input(Description = "Pull request number")]
    public Input<int> PrNumber { get; set; } = new(0);

    [Input(Description = "Issue number this PR closes (0 when unknown)")]
    public Input<int> IssueNumber { get; set; } = new(0);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Event data payload as JSON (counts, filesFixed, errorReason, ...)")]
    public Input<string?> DataJson { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitReviewFixEventActivity() { }

    public EmitReviewFixEventActivity(ILogger<EmitReviewFixEventActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.GetOrDefault(context) ?? ReviewFixEvents.AppliedFailed;
        var repository = Repository.GetOrDefault(context) ?? "";
        var prNumber = PrNumber.GetOrDefault(context);
        var issueNumber = IssueNumber.GetOrDefault(context);
        var tenantId = ReviewFixEvents.ParseTenantId(TenantId.GetOrDefault(context));
        var data = ParseData(DataJson.GetOrDefault(context));

        var evt = BuildTammaEvent(type, repository, prNumber, issueNumber, tenantId, data);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for pr #{Pr} in {Repo} (issue #{Issue})",
            type, prNumber, repository, issueNumber);

        await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete (see EmitEscalationEventActivity precedent); without this the workflow hangs here forever
        return;
    }

    /// <summary>
    /// Map the review-fix inputs onto a <see cref="TammaEvent"/> expressed as the
    /// engine's transient-list event so the merged drain persists it. Tags carry
    /// the queryable DCB index keys (<c>repository</c>/<c>prNumber</c>/<c>prId</c>/
    /// <c>issueId</c>/<c>tenantId</c>); Data carries the metrics / reason payload.
    /// Pure (no Elsa context) — exposed for unit testing the mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string repository,
        int prNumber,
        int issueNumber,
        Guid? tenantId,
        IReadOnlyDictionary<string, object?>? data)
    {
        var tags = new Dictionary<string, object?>
        {
            ["repository"] = repository,
        };
        if (prNumber > 0)
        {
            tags["prNumber"] = prNumber.ToString();
            tags["prId"] = prNumber.ToString();
        }
        if (issueNumber > 0)
        {
            tags["issueNumber"] = issueNumber.ToString();
            tags["issueId"] = issueNumber.ToString();
        }
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        return new TammaEvent
        {
            EventType = type,
            Status = ReviewFixEvents.IsFailureEvent(type) ? "error" : "success",
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
