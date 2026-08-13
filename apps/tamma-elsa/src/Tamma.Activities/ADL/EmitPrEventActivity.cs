using System.Diagnostics.Metrics;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Emits a <c>PR.*</c> DCB event (Story 2.8 AC6 / FR-20) for the audit trail by
/// appending a <see cref="TammaEvent"/> to the workflow's <c>tamma:events</c>
/// transient list via <see cref="TammaEventEmitter.Emit"/>. The merged engine
/// event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes
/// that list <i>durably</i> to the tenant <c>domain_events</c> store after this
/// activity runs — the drain resolves the tenant from the workflow scope (the
/// PR workflow stamps a <c>TenantId</c> variable). The event therefore persists
/// without this activity holding any DB / repository dependency of its own
/// (none is registered in the Elsa engine — the prior direct
/// <c>IEventRepository</c> wiring was inert, silently dropping every PR event).
///
/// <para>On the success transition it also increments the
/// <c>prs_created_total</c> OTel counter (epics.md:1486).</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Emit PR Event",
    "Emit a PR.CREATED.SUCCESS / PR.CREATED.FAILED DCB event for the audit trail",
    Kind = ActivityKind.Task
)]
public class EmitPrEventActivity : Activity
{
    /// <summary>Meter name — pinned so dashboards stay stable.</summary>
    public const string MeterName = "Tamma.PullRequest";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> PrsCreated = Meter.CreateCounter<long>(
        "prs_created_total",
        unit: "{pr}",
        description: "Total pull requests created by the autonomous loop, tagged by repository and draft state.");

    /// <summary>In-process running total since process start (lets tests assert increments).</summary>
    private static long _prsCreatedTotal;
    public static long PrsCreatedTotal => Interlocked.Read(ref _prsCreatedTotal);

    private readonly ILogger<EmitPrEventActivity>? _logger;

    [Input(Description = "Event type — PR.CREATED.SUCCESS or PR.CREATED.FAILED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Issue number this PR closes")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Created/updated PR number (0 when none)")]
    public Input<int> PrNumber { get; set; } = new(0);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Event data payload as JSON (url, base/head, filesChanged, lines, coverage, reviewers, labels, isDraft, durationMs, error)")]
    public Input<string?> DataJson { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitPrEventActivity() { }

    public EmitPrEventActivity(ILogger<EmitPrEventActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.GetOrDefault(context) ?? PrEvents.CreatedFailed;
        var issueNumber = IssueNumber.GetOrDefault(context);
        var repository = Repository.GetOrDefault(context) ?? "";
        var prNumber = PrNumber.GetOrDefault(context);
        var tenantId = PrEvents.ParseTenantId(TenantId.GetOrDefault(context));
        var dataJson = DataJson.GetOrDefault(context);

        var data = ParseData(dataJson);

        // Metric first — it must fire on the success transition.
        if (type == PrEvents.CreatedSuccess)
            RecordCreated(repository, data);

        // Build the DCB event and hand it to the emitter; the engine drain
        // flushes tamma:events durably to domain_events after this activity.
        var evt = BuildTammaEvent(type, issueNumber, repository, prNumber, tenantId, data);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for issue #{Issue} pr #{Pr} in {Repo}",
            type, issueNumber, prNumber, repository);

        await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete (see EmitEscalationEventActivity precedent); without this the workflow hangs here forever
        return;
    }

    /// <summary>
    /// Map the PR event inputs onto a <see cref="TammaEvent"/> — the same DCB
    /// shape <see cref="PrEvents.BuildEvent"/> built for the (now-removed)
    /// repository path, expressed as the engine's transient-list event so the
    /// merged drain persists it. Tags carry the queryable DCB index keys
    /// (<c>issueId</c>/<c>issueNumber</c>/<c>repository</c>/<c>prNumber</c>/
    /// <c>tenantId</c>); <c>Data</c> carries the metrics payload. Exposed for
    /// unit testing the mapping. Pure (no Elsa context).
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        int issueNumber,
        string repository,
        int prNumber,
        Guid? tenantId,
        IReadOnlyDictionary<string, object?>? data)
    {
        var tags = new Dictionary<string, object?>
        {
            ["issueId"] = issueNumber.ToString(),
            ["issueNumber"] = issueNumber.ToString(),
            ["repository"] = repository,
        };
        if (prNumber > 0) tags["prNumber"] = prNumber.ToString();
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        return new TammaEvent
        {
            EventType = type,
            Status = type == PrEvents.CreatedFailed ? "error" : "success",
            Tags = tags,
            Data = data is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(data),
        };
    }

    private void RecordCreated(string repository, IReadOnlyDictionary<string, object?>? data)
    {
        Interlocked.Increment(ref _prsCreatedTotal);
        var isDraft = data is not null && data.TryGetValue("isDraft", out var d) && d is true;
        PrsCreated.Add(1,
            new KeyValuePair<string, object?>("repository", repository),
            new KeyValuePair<string, object?>("draft", isDraft));
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
            var dict = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, object?>>(json);
            return dict;
        }
        catch
        {
            return null;
        }
    }
}
