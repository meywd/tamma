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
    /// Load the projector cursor from the control-plane store (creating an
    /// at-zero in-memory default when no row exists yet). Cursor is always
    /// CP-resident (mirrors <c>alert_evaluator_cursor</c>).
    /// </summary>
    Task<AuditProjectorCursor> LoadCursorAsync(
        ControlPlaneDbContext cp, string projectorId, CancellationToken ct = default);

    /// <summary>
    /// Upsert the projector cursor's per-stream high-water marks into the
    /// control-plane store.
    /// </summary>
    Task SaveCursorAsync(
        ControlPlaneDbContext cp,
        string projectorId,
        long lastDomainSeq,
        long lastPlatformSeq,
        DateTime updatedAt,
        CancellationToken ct = default);
}
