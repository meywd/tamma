using System.Diagnostics.Metrics;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Repositories;

namespace Tamma.Activities.ADL;

/// <summary>
/// Emits a <c>PR.*</c> DCB event (Story 2.8 AC6 / FR-20) into <c>domain_events</c>
/// via <see cref="IEventRepository"/>, modelled on
/// <c>TenantLifecycle/EmitDeletedSuccessActivity</c>.
///
/// <para>On the success transition it also increments the
/// <c>prs_created_total</c> OTel counter (epics.md:1486).</para>
///
/// <para>FR-19e — observability failures MUST NOT block PR creation. The event
/// append is best-effort: if <see cref="IEventRepository"/> is not registered in
/// the host (the Elsa engine does not register it today) or the append throws,
/// the activity logs a warning and completes normally. The PR has already been
/// created; failing here would defeat the whole point of the failure edge.</para>
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
    private readonly IEventRepository? _events;

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

    public EmitPrEventActivity(ILogger<EmitPrEventActivity> logger, IEventRepository events)
    {
        _logger = logger;
        _events = events;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.Get(context) ?? PrEvents.CreatedFailed;
        var issueNumber = IssueNumber.Get(context);
        var repository = Repository.Get(context) ?? "";
        var prNumber = PrNumber.Get(context);
        var tenantId = PrEvents.ParseTenantId(TenantId.Get(context));
        var dataJson = DataJson.Get(context);

        var data = ParseData(dataJson);

        // Metric first — it must fire even if the durable append is unavailable.
        if (type == PrEvents.CreatedSuccess)
            RecordCreated(repository, data);

        // Resolve the repository from DI when constructed by Elsa (the
        // [Activity] sweep uses the parameterless ctor, leaving _events null).
        var events = _events ?? context.GetService<IEventRepository>();
        if (events is null)
        {
            _logger?.LogWarning(
                "PR event {Type} not persisted (IEventRepository unavailable) — issue #{Issue} pr #{Pr}",
                type, issueNumber, prNumber);
            return;
        }

        try
        {
            var evt = PrEvents.BuildEvent(
                type,
                issueNumber,
                repository,
                prNumber > 0 ? prNumber : null,
                tenantId,
                data);

            await events.AppendAsync(evt);

            _logger?.LogInformation(
                "Emitted {Type} for issue #{Issue} pr #{Pr} in {Repo}",
                type, issueNumber, prNumber, repository);
        }
        catch (Exception ex)
        {
            // FR-19e — never block PR creation on an audit-emit failure.
            _logger?.LogWarning(ex,
                "Failed to persist PR event {Type} (continuing) — issue #{Issue}",
                type, issueNumber);
        }
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
