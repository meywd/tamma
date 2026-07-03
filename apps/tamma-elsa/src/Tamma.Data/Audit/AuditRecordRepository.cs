using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tamma.Core.Audit;
using Tamma.Data.Entities;

namespace Tamma.Data.Audit;

/// <summary>
/// Story 37-1 — default <see cref="IAuditRecordRepository"/>. Insert-if-absent
/// on the UNIQUE <c>source_event_id</c> index makes the projection idempotent
/// and replay-safe; cursor load/save mirrors <c>AlertRuleEvaluator</c>.
/// Stateless — safe as a singleton; the caller supplies the per-call context.
///
/// <para>Story 37-2 — the insert path now CHAINS each record: under a per-scope
/// Postgres advisory lock it reads the chain head, sets
/// <see cref="AuditRecord.ChainSequence"/> / <see cref="AuditRecord.PrevRecordHash"/>,
/// and computes <see cref="AuditRecord.RecordHash"/> =
/// <c>SHA-256(prev ‖ canonical(record))</c> — atomically with the insert — so
/// concurrent appends to one chain stay strictly monotonic and tamper-evident.</para>
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

        var scope = ScopeFor(context, record);

        // On real Postgres, serialize the head-read + insert for this chain under
        // pg_advisory_xact_lock so two concurrent appends can't fork the sequence.
        if (context.Database.IsNpgsql())
        {
            var strategy = context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await context.Database
                    .BeginTransactionAsync(ct).ConfigureAwait(false);

                await context.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock({0})",
                    new object[] { scope.AdvisoryLockKey() }, ct).ConfigureAwait(false);

                // Re-check inside the lock — a concurrent projector may have won.
                var stillAbsent = !await context.Set<AuditRecord>().AsNoTracking()
                    .AnyAsync(r => r.SourceEventId == record.SourceEventId, ct)
                    .ConfigureAwait(false);
                if (!stillAbsent)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return false;
                }

                await AssignChainAsync(context, record, scope, ct).ConfigureAwait(false);
                context.Set<AuditRecord>().Add(record);
                try
                {
                    await context.SaveChangesAsync(ct).ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    return true;
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    context.Entry(record).State = EntityState.Detached;
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return false;
                }
            }).ConfigureAwait(false);
        }

        // Non-Postgres (in-memory/SQLite unit tests) — no advisory lock, no
        // explicit transaction. Single-threaded test drivers, so a plain
        // head-read + assign + save preserves the chain invariants.
        await AssignChainAsync(context, record, scope, ct).ConfigureAwait(false);
        context.Set<AuditRecord>().Add(record);
        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            context.Entry(record).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>
    /// Read the chain head for the record's scope and assign
    /// <c>ChainSequence</c> / <c>PrevRecordHash</c> / <c>RecordHash</c>. Only
    /// already-chained rows count toward the head (a null <c>ChainSequence</c> is
    /// a pre-37-2 legacy row awaiting backfill and must not anchor the head).
    /// </summary>
    private static async Task AssignChainAsync(
        DbContext context, AuditRecord record, AuditChainScope scope, CancellationToken ct)
    {
        var head = await context.Set<AuditRecord>().AsNoTracking()
            .Where(r => r.ChainSequence != null)
            .OrderByDescending(r => r.ChainSequence)
            .Select(r => new { r.ChainSequence, r.RecordHash })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        record.ChainSequence = (head?.ChainSequence ?? 0) + 1;
        record.PrevRecordHash = head?.RecordHash ?? AuditChainGenesis.HashHex;
        var view = AuditRecordChainMapper.ToView(record, scope, prevHash: record.PrevRecordHash);
        record.RecordHash = AuditChainHasher.ComposeHex(
            record.PrevRecordHash, AuditRecordCanonicalizer.ToBytes(view));
    }

    /// <summary>
    /// The chain scope of a record being inserted into <paramref name="context"/>.
    /// A <see cref="TenantDbContext"/> insert is that tenant's chain; anything
    /// else (control-plane platform + single-user rows) is the platform chain.
    /// </summary>
    private static AuditChainScope ScopeFor(DbContext context, AuditRecord record) =>
        context is TenantDbContext && record.TenantId is Guid tid
            ? AuditChainScope.ForTenant(tid)
            : AuditChainScope.Platform;

    /// <inheritdoc />
    public async Task<AuditProjectorCursor> LoadCursorAsync(
        ControlPlaneDbContext cp, string projectorId, Guid tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cp);
        var row = await cp.AuditProjectorCursors.AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.ProjectorId == projectorId && c.TenantId == tenantId, ct)
            .ConfigureAwait(false);
        return row ?? new AuditProjectorCursor
        {
            ProjectorId = projectorId,
            TenantId = tenantId,
            LastDomainSequenceNumber = 0L,
            LastPlatformSequenceNumber = 0L,
        };
    }

    /// <inheritdoc />
    public async Task SaveCursorAsync(
        ControlPlaneDbContext cp,
        string projectorId,
        Guid tenantId,
        long lastDomainSeq,
        long lastPlatformSeq,
        DateTime updatedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cp);
        var existing = await cp.AuditProjectorCursors
            .FirstOrDefaultAsync(
                c => c.ProjectorId == projectorId && c.TenantId == tenantId, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            cp.AuditProjectorCursors.Add(new AuditProjectorCursor
            {
                ProjectorId = projectorId,
                TenantId = tenantId,
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
