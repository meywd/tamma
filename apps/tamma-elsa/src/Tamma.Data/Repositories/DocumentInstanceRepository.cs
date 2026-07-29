using Microsoft.EntityFrameworkCore;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 39-11 — the sole writer/reader of <c>document_instances</c> (AC2).
/// Tenant-scoped in the <see cref="ConventionRepository"/> style: routes through
/// <see cref="ITenantDbContextFactory"/> with the ambient <see cref="ITenantContext"/>
/// tenant, and carries an explicit <c>TenantId</c> predicate on every read/write
/// (defence-in-depth for the shared-DB phase; the per-tenant schema is the real
/// isolation plane).
/// </summary>
public class DocumentInstanceRepository(
    ITenantDbContextFactory tenantDbFactory,
    ITenantContext tenantContext) : IDocumentInstanceRepository
{
    private Guid RequireTenantId() => tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "DocumentInstanceRepository requires an ambient tenant id. document_instances " +
            "is tenant-resident; the per-tenant DB factory routes reads/writes through the " +
            "calling tenant's physical database (single-user binds a personal tenant up-front).");

    public async Task<DocumentInstance> InsertAsync(
        Guid tenantId, DocumentEnvelope envelope, Guid? correlatingEventId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        // D5 — write-time validation is the registry, fail-loud. Resolve throws
        // DOCUMENT.TYPE.UNKNOWN / DOCUMENT.TYPE.NOT_REGISTERED on a bad key; a
        // failing body throws DOCUMENT.STORE.INVALID_BODY. The store cannot contain
        // a document it cannot validate — nothing is persisted on a violation.
        var documentType = DocumentTypeRegistry.Resolve(envelope.Type);
        var validation = documentType.Validate(envelope.Payload);
        if (!validation.IsValid)
            throw new TammaError(
                "DOCUMENT.STORE.INVALID_BODY",
                $"Document body failed '{envelope.Type}' validation: " +
                string.Join("; ", validation.Violations.Select(v => $"{v.Code}: {v.Message}")),
                new Dictionary<string, object?>
                {
                    ["type"] = envelope.Type,
                    ["documentId"] = envelope.Id,
                    ["violations"] = validation.Violations
                        .Select(v => new { code = v.Code, message = v.Message }).ToArray(),
                },
                retryable: false,
                severity: TammaErrorSeverity.High);

        // 41-1c D2 — the envelope's Audience is authoritative for the store
        // column. CreateDraft already copies payload → envelope, so a divergence
        // can only come from a hand-built envelope: fail it loud rather than
        // persist a row whose column contradicts its body; backfill a missing
        // envelope copy from the payload so the column never silently lags.
        var payloadAudience = DocumentEnvelope.ReadPayloadAudience(envelope.Payload);
        if (envelope.Audience is not null && payloadAudience is not null
            && !string.Equals(envelope.Audience, payloadAudience, StringComparison.Ordinal))
            throw new TammaError(
                "PROSE_AUDIENCE_ENVELOPE_MISMATCH",
                $"Envelope audience '{envelope.Audience}' disagrees with the payload audience " +
                $"'{payloadAudience}' — the store column mirrors the payload, never diverges.",
                new Dictionary<string, object?>
                {
                    ["documentId"] = envelope.Id,
                    ["audience"] = envelope.Audience,
                    ["payloadAudience"] = payloadAudience,
                },
                retryable: false,
                severity: TammaErrorSeverity.High);

        // 41-1c follow-up (adversarial review 2026-07-29) — the audience COLUMN is
        // type-agnostic by design (any document type may carry a valid tag; the
        // lineage filter works across types), but its vocabulary is closed: the
        // effective value (envelope-authoritative, payload fallback — exactly what
        // the row below persists) must be a ProseAudience wire string. Prose bodies
        // are already vocabulary-checked by ProseDocumentType.Validate above; this
        // gate closes the hand-built-envelope hole where a non-prose envelope (whose
        // validator never looks at audience) would persist audience='junk'.
        var effectiveAudience = envelope.Audience ?? payloadAudience;
        if (effectiveAudience is not null
            && !ProseAudienceExtensions.TryParse(effectiveAudience, out _))
            throw new TammaError(
                ProseDocumentType.AudienceOutOfVocabulary,
                $"Envelope audience '{effectiveAudience}' is not in the closed ProseAudience vocabulary " +
                $"({string.Join(", ", Enum.GetValues<ProseAudience>().Select(a => a.ToWire()))}) — " +
                "the store never persists an out-of-vocabulary audience column.",
                new Dictionary<string, object?>
                {
                    ["documentId"] = envelope.Id,
                    ["type"] = envelope.Type,
                    ["audience"] = effectiveAudience,
                },
                retryable: false,
                severity: TammaErrorSeverity.High);

        var dbTenant = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(dbTenant, ct);

        var now = DateTime.UtcNow;
        var revision = 1;

        // D4 — supersession is a branch of insert, in one transaction. Load the
        // prior (tenant-checked) row and flip it to superseded; the unique filtered
        // index on supersedes_document_id keeps the chain linear (two rows can't
        // supersede the same prior — a concurrent second superseding insert 23505s).
        if (envelope.SupersedesDocumentId is Guid priorId)
        {
            var prior = await db.Documents.IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.Id == priorId && d.TenantId == tenantId, ct);
            if (prior is null)
                throw new TammaError(
                    "DOCUMENT.STORE.NOT_FOUND",
                    $"Cannot supersede document '{priorId}': no such row in this tenant's store.",
                    new Dictionary<string, object?> { ["documentId"] = priorId },
                    retryable: false,
                    severity: TammaErrorSeverity.High);

            revision = prior.Revision + 1;
            prior.Status = DocumentInstanceStatus.Superseded.ToWire();
            prior.UpdatedAt = now;
        }

        var row = new DocumentInstance
        {
            Id = envelope.Id,
            DocumentType = envelope.Type,
            IssueId = envelope.IssueId,
            ProducedByRole = envelope.ProducedBy.Role,
            ProducedByAction = envelope.ProducedBy.Action,
            ProducedByWorkflow = envelope.ProducedBy.WorkflowDefinitionId,
            SchemaVersion = envelope.SchemaVersion,
            CorrelationId = envelope.CorrelationId,
            Revision = revision,
            Status = DocumentInstanceStatusExtensions.FromState(envelope.State).ToWire(),
            SupersedesDocumentId = envelope.SupersedesDocumentId,
            ParentDocumentId = envelope.ParentDocumentId,
            CorrelatingEventId = correlatingEventId,
            TenantId = tenantId,
            Audience = effectiveAudience,
            BodyJson = envelope.Payload.GetRawText(),
            CreatedAt = DateTime.SpecifyKind(envelope.CreatedAt.UtcDateTime, DateTimeKind.Utc),
            UpdatedAt = now,
        };

        db.Documents.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<DocumentInstance> SetStatusAsync(
        Guid tenantId, Guid documentId, DocumentInstanceStatus status,
        Guid? correlatingEventId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        // Superseded is set exclusively by the revision write (D4) — never a
        // status transition. Reject loud rather than corrupt the chain invariant.
        if (status == DocumentInstanceStatus.Superseded)
            throw new TammaError(
                "DOCUMENT.STORE.ILLEGAL_STATUS",
                "SetStatusAsync cannot set 'superseded' — supersession is set only by the " +
                "revision write (InsertAsync with a supersedesDocumentId).",
                new Dictionary<string, object?> { ["documentId"] = documentId },
                retryable: false,
                severity: TammaErrorSeverity.High);

        var dbTenant = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(dbTenant, ct);

        var row = await db.Documents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.TenantId == tenantId, ct);
        if (row is null)
            throw new TammaError(
                "DOCUMENT.STORE.NOT_FOUND",
                $"No document '{documentId}' in this tenant's store.",
                new Dictionary<string, object?> { ["documentId"] = documentId },
                retryable: false,
                severity: TammaErrorSeverity.High);

        row.Status = status.ToWire();
        if (correlatingEventId is not null)
            row.CorrelatingEventId = correlatingEventId;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<DocumentInstance?> GetByIdAsync(Guid tenantId, Guid documentId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        var dbTenant = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(dbTenant, ct);
        return await db.Documents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.TenantId == tenantId, ct);
    }

    public async Task<IReadOnlyList<DocumentInstance>> ListByIssueAsync(
        Guid tenantId, string issueId, string? audience, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        var dbTenant = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(dbTenant, ct);
        var query = db.Documents.IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId && d.IssueId == issueId);
        // 41-1c AC3 — null means UNFILTERED (the pre-41-1c behaviour, provably
        // unchanged for every existing caller); a value filters in SQL on the
        // partial (issue_id, audience) index.
        if (audience is not null)
            query = query.Where(d => d.Audience == audience);
        return await query
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Revision)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DocumentInstance>> GetLatestAcceptedAsync(
        Guid tenantId, string issueId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        var acceptedWire = DocumentInstanceStatus.Accepted.ToWire();
        var dbTenant = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(dbTenant, ct);

        var accepted = await db.Documents.IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId && d.IssueId == issueId && d.Status == acceptedWire)
            .ToListAsync(ct);

        // Highest revision per document type (D10). The single write door + the
        // unique filtered supersedes index make at most one non-superseded accepted
        // row per chain, so this is deterministic (≤1 per type).
        return accepted
            .GroupBy(d => d.DocumentType, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(d => d.Revision).First())
            .OrderBy(d => d.CreatedAt)
            .ToList();
    }
}
