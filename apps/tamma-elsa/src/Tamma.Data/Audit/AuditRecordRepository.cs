using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tamma.Data.Entities;

namespace Tamma.Data.Audit;

/// <summary>
/// Story 37-1 — default <see cref="IAuditRecordRepository"/>. Insert-if-absent
/// on the UNIQUE <c>source_event_id</c> index makes the projection idempotent
/// and replay-safe; cursor load/save mirrors <c>AlertRuleEvaluator</c>.
/// Stateless — safe as a singleton; the caller supplies the per-call context.
/// </summary>
public sealed class AuditRecordRepository : IAuditRecordRepository
{
    /// <inheritdoc />
    public async Task<bool> InsertIfAbsentAsync(
        DbContext context, AuditRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(record);

        // Pre-check keeps the common (already-projected) path off the
        // exception machinery; the UNIQUE index is the actual guarantee that
        // closes the concurrent-insert race below.
        var exists = await context.Set<AuditRecord>().AsNoTracking()
            .AnyAsync(r => r.SourceEventId == record.SourceEventId, ct)
            .ConfigureAwait(false);
        if (exists) return false;

        context.Set<AuditRecord>().Add(record);
        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent projector won the race — already projected. Detach
            // the rejected entry so the context can be reused cleanly.
            context.Entry(record).State = EntityState.Detached;
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<AuditProjectorCursor> LoadCursorAsync(
        ControlPlaneDbContext cp, string projectorId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cp);
        var row = await cp.AuditProjectorCursors.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProjectorId == projectorId, ct)
            .ConfigureAwait(false);
        return row ?? new AuditProjectorCursor
        {
            ProjectorId = projectorId,
            LastDomainSequenceNumber = 0L,
            LastPlatformSequenceNumber = 0L,
        };
    }

    /// <inheritdoc />
    public async Task SaveCursorAsync(
        ControlPlaneDbContext cp,
        string projectorId,
        long lastDomainSeq,
        long lastPlatformSeq,
        DateTime updatedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cp);
        var existing = await cp.AuditProjectorCursors
            .FirstOrDefaultAsync(c => c.ProjectorId == projectorId, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            cp.AuditProjectorCursors.Add(new AuditProjectorCursor
            {
                ProjectorId = projectorId,
                LastDomainSequenceNumber = lastDomainSeq,
                LastPlatformSequenceNumber = lastPlatformSeq,
                UpdatedAt = updatedAt,
            });
        }
        else
        {
            existing.LastDomainSequenceNumber = lastDomainSeq;
            existing.LastPlatformSequenceNumber = lastPlatformSeq;
            existing.UpdatedAt = updatedAt;
        }
        await cp.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg &&
        pg.SqlState == PostgresErrorCodes.UniqueViolation;
}
