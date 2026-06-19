using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Audit;

/// <summary>
/// Story 37-1 — insert-if-absent writer for the curated <c>audit_records</c>
/// read-model, plus the projector-cursor load/save. The same contract serves
/// the control-plane and per-tenant stores; the caller selects the context
/// (CP vs tenant) and passes it in, so routing (Story 37-1 AC11) stays in the
/// projector while the dedup + cursor mechanics live here.
/// </summary>
public interface IAuditRecordRepository
{
    /// <summary>
    /// Insert one curated row if no row with the same
    /// <see cref="AuditRecord.SourceEventId"/> exists yet. Returns <c>true</c>
    /// when a row was inserted, <c>false</c> when the source event was already
    /// projected (idempotent — a re-scan after a crash is a no-op, AC8). A
    /// unique-violation race resolves to <c>false</c> (already projected).
    /// </summary>
    Task<bool> InsertIfAbsentAsync(
        DbContext context, AuditRecord record, CancellationToken ct = default);

    /// <summary>
    /// Load one projector cursor row from the control-plane store, keyed by
    /// <c>(projectorId, tenantId)</c>. Pass
    /// <see cref="AuditProjectorCursor.PlatformSentinel"/> for the platform /
    /// shared-DB row, or a real tenant id for that tenant's per-schema domain
    /// stream. Creates an at-zero in-memory default when no row exists yet. The
    /// cursor table is always CP-resident (C1: one domain high-water mark per
    /// tenant, the platform stream on the sentinel row).
    /// </summary>
    Task<AuditProjectorCursor> LoadCursorAsync(
        ControlPlaneDbContext cp, string projectorId, Guid tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Upsert one projector cursor row's high-water marks into the control-plane
    /// store, keyed by <c>(projectorId, tenantId)</c>. For a per-tenant row only
    /// <paramref name="lastDomainSeq"/> advances; for the
    /// <see cref="AuditProjectorCursor.PlatformSentinel"/> row both the
    /// platform-stream and shared-DB domain-fallback marks advance.
    /// </summary>
    Task SaveCursorAsync(
        ControlPlaneDbContext cp,
        string projectorId,
        Guid tenantId,
        long lastDomainSeq,
        long lastPlatformSeq,
        DateTime updatedAt,
        CancellationToken ct = default);
}
