using System.Text.Json;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Resume;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-10 (AC5, Design Decisions D1/D7) — the REAL re-entry service. Reads
/// (a) the latest ACCEPTED instance per type via 39-11's
/// <see cref="IDocumentInstanceRepository.GetLatestAcceptedAsync"/> (in-process, never
/// HTTP) and (b) the issue's <c>DOCUMENT.*</c>/<c>APPROVAL.*</c> events via the 4-7
/// <see cref="IEventRepository.QueryEventsAsync"/> query surface, maps both onto the
/// Core-visible neutral shapes, and delegates the fold to
/// <see cref="LifecycleResumeCalculator"/>. Registered as the default
/// <see cref="ILifecycleReEntryService"/> in both hosts now that 39-11 has merged;
/// <see cref="NullLifecycleReEntryService"/> is the config-flag fallback (D7).
/// </summary>
public sealed class LifecycleReEntryService : ILifecycleReEntryService
{
    private readonly IDocumentInstanceRepository _documents;
    private readonly IEventRepository _events;
    private readonly ITenantContext _tenantContext;

    /// <summary>Per-family cap on the 4-7 event read; a single issue's lifecycle
    /// event slice is small, but the tenant-wide prefix scan is bounded defensively.</summary>
    private const int MaxEventsPerFamily = 2000;

    public LifecycleReEntryService(
        IDocumentInstanceRepository documents,
        IEventRepository events,
        ITenantContext tenantContext)
    {
        _documents = documents;
        _events = events;
        _tenantContext = tenantContext;
    }

    public async Task<LifecycleResumePosition> ReconstructAsync(
        Guid? tenantId, string issueId, string documentTypeKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(issueId))
            return LifecycleResumePosition.Fresh(documentTypeKey, "No issue id supplied; running fresh.");

        var tenant = ResolveTenant(tenantId);

        // (a) 39-11 latest-accepted read (in-process repository method, per its AC4).
        var accepted = await _documents.GetLatestAcceptedAsync(tenant, issueId, ct).ConfigureAwait(false);
        var acceptedRef = accepted
            .Where(d => string.Equals(d.DocumentType, documentTypeKey, StringComparison.Ordinal))
            .Select(d => new AcceptedDocumentRef(d.Id, d.DocumentType, d.Revision))
            .FirstOrDefault();

        // (b) 4-7 DCB event slice, mapped to the neutral fold DTO, oldest-first.
        var rows = await LoadEventRowsAsync(tenant, issueId, ct).ConfigureAwait(false);

        return LifecycleResumeCalculator.Reconstruct(documentTypeKey, acceptedRef, rows);
    }

    public async Task<DocumentEnvelope?> GetDocumentBodyAsync(
        Guid? tenantId, Guid documentId, CancellationToken ct)
    {
        if (documentId == Guid.Empty) return null;
        var tenant = ResolveTenant(tenantId);
        var row = await _documents.GetByIdAsync(tenant, documentId, ct).ConfigureAwait(false);
        return row is null ? null : MapEnvelope(row);
    }

    // ── Tenant resolution ─────────────────────────────────────────────────

    private Guid ResolveTenant(Guid? tenantId)
    {
        if (tenantId is Guid t && t != Guid.Empty) return t;
        // Single-user binds a personal tenant up-front; the ambient context carries it.
        return _tenantContext.TenantId
            ?? throw new TammaError(
                "DOCUMENT.REENTRY.NO_TENANT",
                "Re-entry reconstruction requires a tenant id (explicit or ambient); none was available.",
                retryable: false,
                severity: TammaErrorSeverity.High);
    }

    // ── Event read + mapping ──────────────────────────────────────────────

    private async Task<IReadOnlyList<ResumeEventRow>> LoadEventRowsAsync(
        Guid tenant, string issueId, CancellationToken ct)
    {
        var document = await _events
            .QueryEventsAsync(tenant, "DOCUMENT.", typeIsPrefix: true,
                correlationId: null, actor: null, from: null, to: null,
                cursor: null, limit: MaxEventsPerFamily)
            .ConfigureAwait(false);
        var approval = await _events
            .QueryEventsAsync(tenant, "APPROVAL.", typeIsPrefix: true,
                correlationId: null, actor: null, from: null, to: null,
                cursor: null, limit: MaxEventsPerFamily)
            .ConfigureAwait(false);

        return document.Events.Concat(approval.Events)
            .Select(e => (Event: e, Tags: ParseTags(e.Tags)))
            .Where(x => string.Equals(ReadTag(x.Tags, "issueId"), issueId, StringComparison.Ordinal))
            // Total order via the BIGSERIAL sequence — immune to same-ms CreatedAt collisions.
            .OrderBy(x => x.Event.SequenceNumber)
            .Select(x => MapRow(x.Event, x.Tags))
            .ToList();
    }

    private static ResumeEventRow MapRow(DomainEvent e, JsonElement tags) => new(
        Type: e.Type,
        CreatedAtUtc: DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc),
        DocumentId: ReadGuidTag(tags, "documentId"),
        DocumentTypeKey: ReadTag(tags, "documentType"),
        SessionId: ReadGuidTag(tags, "sessionId"),
        Revision: ReadIntTag(tags, "round"));

    private static JsonElement ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return default;
        try
        {
            using var doc = JsonDocument.Parse(tagsJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? ReadTag(JsonElement tags, string name)
        => tags.ValueKind == JsonValueKind.Object
           && tags.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static Guid? ReadGuidTag(JsonElement tags, string name)
        => Guid.TryParse(ReadTag(tags, name), out var g) ? g : null;

    private static int? ReadIntTag(JsonElement tags, string name)
    {
        if (tags.ValueKind != JsonValueKind.Object || !tags.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s,
            _ => null,
        };
    }

    // ── Store row → envelope reconstruction (guard path) ──────────────────

    private static DocumentEnvelope MapEnvelope(DocumentInstance row)
    {
        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(row.BodyJson) ? "{}" : row.BodyJson);
            payload = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse("{}");
            payload = doc.RootElement.Clone();
        }

        return new DocumentEnvelope
        {
            Id = row.Id,
            Type = row.DocumentType,
            SchemaVersion = row.SchemaVersion,
            IssueId = row.IssueId,
            CorrelationId = row.CorrelationId ?? string.Empty,
            ParentDocumentId = row.ParentDocumentId,
            SupersedesDocumentId = row.SupersedesDocumentId,
            // Reconstruct the producer directly (no eligibility re-assertion — the row
            // was already validated at write time).
            ProducedBy = new DocumentProducer
            {
                Role = row.ProducedByRole,
                Action = row.ProducedByAction,
                WorkflowDefinitionId = string.IsNullOrWhiteSpace(row.ProducedByWorkflow) ? "llm-call" : row.ProducedByWorkflow!,
            },
            State = MapState(row.Status),
            CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
            UpdatedAt = DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc),
            Payload = payload,
        };
    }

    /// <summary>Reverse of <c>DocumentInstanceStatusExtensions.FromState</c> for the guard-path
    /// reconstruction — the store status wire onto the envelope <see cref="DocumentState"/>.</summary>
    private static DocumentState MapState(string? status) => status switch
    {
        "draft" => DocumentState.Draft,
        "validated" => DocumentState.Validated,
        "in_review" => DocumentState.Reviewed,
        "accepted" => DocumentState.Accepted,
        "rejected" => DocumentState.Rejected,
        "escalated" => DocumentState.Escalated,
        // 'superseded' has no envelope state of its own; treat as a validated draft
        // for reconstruction purposes (the guard only re-enters non-superseded rows).
        "superseded" => DocumentState.Validated,
        _ => DocumentState.Draft,
    };
}
