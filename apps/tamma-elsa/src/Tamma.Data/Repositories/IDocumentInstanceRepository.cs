using Tamma.Core.Documents;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 39-11 — the SOLE writer + reader of the tenant-resident
/// <c>document_instances</c> store. No other code touches the <c>Documents</c>
/// DbSet (AC2). Immutability is by API absence: there is NO body-update and NO
/// delete method; a revise round is a branch of <see cref="InsertAsync"/> (D4).
/// </summary>
public interface IDocumentInstanceRepository
{
    /// <summary>
    /// The ONLY row-creating method (Design Decisions D4/D5). Validates the
    /// envelope body against the 39-2/39-3/39-4 type registry BEFORE persisting
    /// (invalid → <c>DOCUMENT.STORE.INVALID_BODY</c>, nothing persisted); an
    /// unknown/unregistered type key bubbles the registry error. Supersession is a
    /// branch, not an update: <c>envelope.SupersedesDocumentId == null</c> →
    /// <c>revision = 1</c>; non-null → load the prior (tenant-checked) row, insert
    /// with <c>revision = prior.Revision + 1</c>, and flip the prior row to
    /// <c>superseded</c> in the SAME transaction. The row carries
    /// <paramref name="correlatingEventId"/> (the AC7 store↔stream linkage).
    /// </summary>
    Task<DocumentInstance> InsertAsync(
        Guid tenantId, DocumentEnvelope envelope, Guid? correlatingEventId, CancellationToken ct);

    /// <summary>
    /// Transition an existing row's status ONLY (never body, never revision). Throws
    /// <c>DOCUMENT.STORE.ILLEGAL_STATUS</c> on <see cref="DocumentInstanceStatus.Superseded"/>
    /// (supersession is set exclusively by the revision write, D4) and
    /// <c>DOCUMENT.STORE.NOT_FOUND</c> on a missing/foreign row. Stamps
    /// <paramref name="correlatingEventId"/> when supplied (the AC7 linkage).
    /// </summary>
    Task<DocumentInstance> SetStatusAsync(
        Guid tenantId, Guid documentId, DocumentInstanceStatus status,
        Guid? correlatingEventId, CancellationToken ct);

    /// <summary>Single-document fetch, tenant-checked. Null when missing or foreign.</summary>
    Task<DocumentInstance?> GetByIdAsync(Guid tenantId, Guid documentId, CancellationToken ct);

    /// <summary>
    /// Every revision of every type for the issue, oldest-first (lineage source).
    /// <paramref name="audience"/> (Story 41-1c AC3) is an OPTIONAL filter on the
    /// stored audience tag: <c>null</c> means UNFILTERED — every existing caller
    /// (including 39-10's re-entry read path) keeps its exact behaviour; a value
    /// returns only rows whose <c>audience</c> column equals it.
    /// </summary>
    Task<IReadOnlyList<DocumentInstance>> ListByIssueAsync(
        Guid tenantId, string issueId, string? audience, CancellationToken ct);

    /// <summary>
    /// Design Decision D10 — the 39-10 lockstep in-process read: the single latest
    /// ACCEPTED instance per document type (≤1 per type). Superseded / draft /
    /// in_review / rejected / escalated rows never appear.
    /// </summary>
    Task<IReadOnlyList<DocumentInstance>> GetLatestAcceptedAsync(Guid tenantId, string issueId, CancellationToken ct);
}
