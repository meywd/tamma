using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 41-2 (Design Decision D7) — the ONE shared domain-event emitter for the Epic 41
/// document-lifecycle binding batch.
///
/// <para><b>Why one activity instead of five.</b> The house pattern is one
/// <c>Emit{Family}EventActivity</c> per DCB family (28 such classes exist), because each legacy
/// family carries a family-specific payload the emitter had to preserve — e.g.
/// <see cref="Tamma.Activities.Decomposition.EmitDecompositionEventActivity"/>'s
/// <c>SubtaskCount</c>. None of the Epic 41 producer families has a legacy consumer with a
/// payload shape to preserve: each emits <c>{FAMILY}.STARTED</c> / <c>.DRAFTED</c> /
/// <c>.ACCEPTED</c> / <c>.FAILED</c> around a thin binding over <c>document-lifecycle</c>, with
/// the same tag set (<c>issueId</c> / <c>repository</c> / <c>tenantId</c> /
/// <c>correlationId</c> / <c>documentId</c>) and a free <c>dataJson</c> payload. Five
/// near-identical copies would be duplication with no upside, so the family is an INPUT and the
/// per-story artifact is a constants file (<see cref="AcceptanceCriteriaEvents"/>,
/// <c>AdrEvents</c>, …).</para>
///
/// <para><b>Status is derived generically from the type suffix</b>
/// (<see cref="StatusForEvent"/>): <c>.FAILED</c> / <c>.REJECTED</c> / <c>.ESCALATED</c> are
/// LOUD error rows, <c>.STARTED</c> is a started row, everything else is a success row — the
/// <c>DecompositionEvents.StatusForEvent</c> / <c>DocumentEvents.StatusForEvent</c> convention,
/// generalised so a degraded exit can never be recorded as a false success.</para>
///
/// <para>Emission goes through <see cref="TammaEventEmitter.Emit"/> onto the workflow's
/// <c>tamma:events</c> transient list; the merged engine drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes it durably to the tenant
/// <c>domain_events</c> store. No repository dependency — an injected one is inert in the Elsa
/// engine and would silently drop every event.</para>
/// </summary>
[Activity(
    "Tamma.Documents",
    "Emit Domain Lifecycle Event",
    "Emit a domain-family DCB event (ACCEPTANCE_CRITERIA.* / ADR.* / …) around a document-lifecycle binding",
    Kind = ActivityKind.Task
)]
public class EmitDomainLifecycleEventActivity : Activity
{
    private readonly ILogger<EmitDomainLifecycleEventActivity>? _logger;

    [Input(Description = "Event type — e.g. ACCEPTANCE_CRITERIA.STARTED / ADR.ACCEPTED (AGGREGATE.ACTION convention)")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Issue / requirement id (the lineage anchor)")]
    public Input<string?> IssueId { get; set; } = new((string?)null);

    [Input(Description = "Repository slug (lineage tag)")]
    public Input<string?> Repository { get; set; } = new((string?)null);

    [Input(Description = "Correlation id (lineage anchor)")]
    public Input<string?> CorrelationId { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Document id the transition concerns (empty before the lifecycle mints one)")]
    public Input<string?> DocumentId { get; set; } = new((string?)null);

    [Input(Description = "Free-text detail for the audit payload (e.g. the typed outcome wire on a failure)")]
    public Input<string?> Detail { get; set; } = new((string?)null);

    [Input(Description = "Optional structured JSON payload (e.g. consumedDocumentIds, criteriaCount)")]
    public Input<string?> DataJson { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitDomainLifecycleEventActivity() { }

    public EmitDomainLifecycleEventActivity(ILogger<EmitDomainLifecycleEventActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.Get(context) ?? "";
        var evt = BuildTammaEvent(
            type,
            IssueId.Get(context),
            Repository.Get(context),
            CorrelationId.Get(context),
            ParseTenantId(TenantId.Get(context)),
            DocumentId.Get(context),
            Detail.Get(context),
            DataJson.Get(context));

        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for issue {Issue} document {Doc}",
            type, IssueId.Get(context), DocumentId.Get(context));

        return default;
    }

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through workflow inputs. Returns
    /// <c>null</c> for empty / single-user / unparseable values (platform-scope events).
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// The generic status convention: a <c>.FAILED</c> / <c>.REJECTED</c> / <c>.ESCALATED</c>
    /// terminal is a LOUD error row, a <c>.STARTED</c> transition is a started row, and every
    /// other transition is a success row. Suffix-driven so a new family gets the right statuses
    /// with no per-family switch to forget.
    /// </summary>
    public static string StatusForEvent(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return "error";

        if (type!.EndsWith(".FAILED", StringComparison.Ordinal) ||
            type.EndsWith(".REJECTED", StringComparison.Ordinal) ||
            type.EndsWith(".ESCALATED", StringComparison.Ordinal))
            return "error";

        return type.EndsWith(".STARTED", StringComparison.Ordinal) ? "started" : "success";
    }

    /// <summary>
    /// Map the domain event inputs onto a <see cref="TammaEvent"/>. Tags carry the queryable DCB
    /// index keys; <c>Data</c> carries the per-transition payload. Pure (no Elsa context);
    /// exposed for unit testing the mapping.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        string? issueId,
        string? repository,
        string? correlationId,
        Guid? tenantId,
        string? documentId,
        string? detail,
        string? dataJson)
    {
        var tags = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(issueId)) tags["issueId"] = issueId;
        if (!string.IsNullOrWhiteSpace(repository)) tags["repository"] = repository;
        if (!string.IsNullOrWhiteSpace(correlationId)) tags["correlationId"] = correlationId;
        if (!string.IsNullOrWhiteSpace(documentId)) tags["documentId"] = documentId;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(detail)) data["detail"] = detail;
        if (!string.IsNullOrWhiteSpace(dataJson)) data["payload"] = dataJson;

        return new TammaEvent
        {
            EventType = type,
            Status = StatusForEvent(type),
            Tags = tags,
            Data = data,
        };
    }
}
