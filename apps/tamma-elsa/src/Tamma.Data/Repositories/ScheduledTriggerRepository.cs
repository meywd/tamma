using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 41-30 — EF-backed implementation of
/// <see cref="IScheduledTriggerRepository"/> over the control-plane
/// <c>scheduled_triggers</c> / <c>scheduled_trigger_fires</c> tables.
///
/// <para><b><see cref="TryClaimFireAsync"/> is the correctness core</b> (D2,
/// Correction 3): the claim is a raw
/// <c>INSERT … ON CONFLICT ("TriggerId","WindowKey") DO NOTHING</c> whose
/// affected-row count IS the answer — 1 = we own this window, 0 = someone
/// already did. It must stay a single INSERT arbitrated by Postgres; a
/// check-then-insert here would reopen the cross-pod race the advisory lock
/// alone cannot durably close (a session-scoped lock dies with a crashed
/// pod's connection).</para>
///
/// <para>Constructed over <see cref="IDbContextFactory{TContext}"/> because
/// the consumer is a singleton hosted service that opens a fresh context per
/// tick step (the <c>TenantCleanupRequestedTrigger</c> composition shape).</para>
/// </summary>
public class ScheduledTriggerRepository(
    IDbContextFactory<ControlPlaneDbContext> dbFactory,
    ILogger<ScheduledTriggerRepository>? logger = null) : IScheduledTriggerRepository
{
    public async Task<IReadOnlyList<Guid>> SnapshotActiveTenantIdsAsync(
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // The landed active-tenant fan-out snapshot
        // (QueuedTaskRepository.ListPendingFromAnyTenantAsync) — copy, don't
        // invent a second one.
        return await db.Tenants
            .AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(ct);
    }

    public async Task<int> MaterialiseTemplatesAsync(
        IReadOnlyList<Guid> activeTenantIds, DateTime nowUtc, CancellationToken ct = default)
    {
        if (activeTenantIds.Count == 0) return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var templates = await db.ScheduledTriggers
            .AsNoTracking()
            .Where(t => t.TenantId == null && t.Enabled)
            .ToListAsync(ct);
        if (templates.Count == 0) return 0;

        var created = 0;
        foreach (var template in templates)
        {
            try
            {
                // Which active tenants already have a concrete row for this
                // (DefinitionId, Name)? Insert the rest, ON CONFLICT DO NOTHING
                // so two pods materialising the same tick stay idempotent.
                var existing = await db.ScheduledTriggers
                    .AsNoTracking()
                    .Where(t => t.TenantId != null
                        && t.DefinitionId == template.DefinitionId
                        && t.Name == template.Name)
                    .Select(t => t.TenantId!.Value)
                    .ToListAsync(ct);
                var existingSet = existing.ToHashSet();

                foreach (var tenantId in activeTenantIds)
                {
                    if (existingSet.Contains(tenantId)) continue;
                    ct.ThrowIfCancellationRequested();

                    created += await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO scheduled_triggers
                            ("Id", "TenantId", "DefinitionId", "Name", "CronExpression",
                             "Enabled", "InputJson", "CreatedAt", "UpdatedAt")
                        VALUES
                            (gen_random_uuid(), {tenantId}, {template.DefinitionId},
                             {template.Name}, {template.CronExpression}, {template.Enabled},
                             CAST({template.InputJson} AS jsonb), {nowUtc}, {nowUtc})
                        ON CONFLICT ("TenantId", "DefinitionId", "Name") DO NOTHING;
                        """, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // MODERATE-4 fix (2026-07-29): per-template failure isolation.
                // A poison template row (e.g. a value the CAST(... AS jsonb)
                // rejects, smuggled in by raw SQL) must not stop the REMAINING
                // templates from materialising — and the service additionally
                // isolates this whole method so a wholesale failure cannot
                // kill the tick either. WARN + skip; the next tick retries.
                logger?.LogWarning(ex,
                    "schedule.materialise.template_failed template={TemplateId} definition={DefinitionId} name={Name} — skipping this template, continuing with the rest",
                    template.Id, template.DefinitionId, template.Name);
            }
        }

        return created;
    }

    public async Task<IReadOnlyList<ScheduledTrigger>> ListEnabledConcreteTriggersAsync(
        IReadOnlyList<Guid> activeTenantIds, CancellationToken ct = default)
    {
        if (activeTenantIds.Count == 0) return Array.Empty<ScheduledTrigger>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ScheduledTriggers
            .AsNoTracking()
            .Where(t => t.Enabled
                && t.TenantId != null
                && activeTenantIds.Contains(t.TenantId!.Value))
            .OrderBy(t => t.TenantId).ThenBy(t => t.Id)
            .ToListAsync(ct);
    }

    public async Task<bool> TryClaimFireAsync(
        ScheduledTriggerFire fire, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // ON CONFLICT DO NOTHING against ux_scheduled_trigger_fires_trigger_window:
        // affected-rows 1 = claimed, 0 = another pod / an earlier run owns it.
        // This single statement is the whole cross-pod, crash-durable dedupe.
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO scheduled_trigger_fires
                ("Id", "TriggerId", "TenantId", "DefinitionId", "WindowKey",
                 "ClaimedAt", "Outcome")
            VALUES
                ({fire.Id}, {fire.TriggerId}, {fire.TenantId}, {fire.DefinitionId},
                 {fire.WindowKey}, {fire.ClaimedAt}, 'claimed')
            ON CONFLICT ("TriggerId", "WindowKey") DO NOTHING;
            """, ct);
        return affected == 1;
    }

    public async Task<string?> GetFireOutcomeAsync(
        Guid triggerId, string windowKey, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Single-row probe on the unique (TriggerId, WindowKey) index —
        // MODERATE-5: the tick asks "is this window already settled?" BEFORE
        // it takes the advisory lock, so a burnt window is re-observed
        // silently (no lock, no claim, no duplicate audit row) instead of
        // re-losing the claim and emitting SCHEDULE.FIRE.SUPPRESSED on every
        // 60-second tick of every pod for the rest of the cadence.
        return await db.ScheduledTriggerFires
            .AsNoTracking()
            .Where(f => f.TriggerId == triggerId && f.WindowKey == windowKey)
            .Select(f => f.Outcome)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ScheduledTriggerFire>> ListStaleClaimedFiresAsync(
        DateTime claimedBeforeUtc, int limit, CancellationToken ct = default)
    {
        if (limit <= 0) return Array.Empty<ScheduledTriggerFire>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // LOW-8: bounded — oldest first, Take(limit). In the normal case this
        // matches nothing (every claim reaches a terminal outcome within one
        // dispatch), so the sweep costs one indexed-ish read per tick.
        return await db.ScheduledTriggerFires
            .AsNoTracking()
            .Where(f => f.Outcome == "claimed" && f.ClaimedAt < claimedBeforeUtc)
            .OrderBy(f => f.ClaimedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<bool> TryMarkFireAbandonedAsync(
        Guid fireId, string detail, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // LOW-8: conditional CAS, same shape as the manual-drain arbiter.
        // Postgres decides which pod's sweep owns the row, so the abandoned
        // fire is announced exactly once fleet-wide; and because the row
        // becomes TERMINAL, the next tick's sweep no longer sees it (this is
        // what keeps the new surface from becoming the very per-tick drip
        // MODERATE-5 was about). DispatchedAt is left as-is — for a burnt
        // manual fire it is the CAS marker, not a dispatch time.
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE scheduled_trigger_fires
            SET "Outcome" = 'failed', "Detail" = {detail}
            WHERE "Id" = {fireId}
              AND "Outcome" = 'claimed';
            """, ct);
        return affected == 1;
    }

    public async Task StampOutcomeAsync(
        Guid fireId, string outcome, string? workflowInstanceId, string? detail,
        DateTime? dispatchedAtUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ScheduledTriggerFires
            .Where(f => f.Id == fireId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.Outcome, outcome)
                .SetProperty(f => f.WorkflowInstanceId, workflowInstanceId)
                .SetProperty(f => f.Detail, detail)
                .SetProperty(f => f.DispatchedAt, dispatchedAtUtc), ct);
    }

    public async Task StampTriggerFiredAsync(
        Guid triggerId, string windowKey, DateTime firedAtUtc, DateTime? nextDueAtUtc,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ScheduledTriggers
            .Where(t => t.Id == triggerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.LastWindowKey, windowKey)
                .SetProperty(t => t.LastFiredAt, firedAtUtc)
                .SetProperty(t => t.NextDueAt, nextDueAtUtc)
                .SetProperty(t => t.UpdatedAt, firedAtUtc), ct);
    }

    public async Task<IReadOnlyList<(ScheduledTriggerFire Fire, ScheduledTrigger Trigger)>>
        ListPendingManualFiresAsync(int limit, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Enabled-trigger filter BEFORE Take so disabled triggers' pending
        // rows (which wait for re-enablement — the 2026-07-29 contract) do
        // not eat the drain budget. NOT a claim: the drain must win
        // TryClaimManualFireForDispatchAsync per row before dispatching.
        var rows = await db.ScheduledTriggerFires
            .AsNoTracking()
            .Where(f => f.Outcome == "claimed"
                && f.DispatchedAt == null
                && f.WindowKey.StartsWith("manual:"))
            .Join(
                db.ScheduledTriggers.AsNoTracking(),
                f => f.TriggerId,
                t => t.Id,
                (f, t) => new { Fire = f, Trigger = t })
            .Where(r => r.Trigger.Enabled)
            .OrderBy(r => r.Fire.ClaimedAt)
            .Take(limit)
            .ToListAsync(ct);

        return rows.Select(r => (r.Fire, r.Trigger)).ToList();
    }

    public async Task<bool> TryClaimManualFireForDispatchAsync(
        Guid fireId, DateTime attemptAtUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // MAJOR-1 fix (2026-07-29): conditional CAS — stamping DispatchedAt
        // while Outcome is still 'claimed' marks "dispatch attempt started"
        // without widening the CHECK-pinned outcome set. Postgres arbitrates:
        // affected-rows 1 = this pod owns the dispatch attempt, 0 = another
        // pod (or an earlier attempt) already does. Because
        // ListPendingManualFiresAsync filters DispatchedAt IS NULL, a crash
        // after this CAS burns the fire (at-most-once) instead of
        // re-dispatching it on every subsequent tick.
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE scheduled_trigger_fires
            SET "DispatchedAt" = {attemptAtUtc}
            WHERE "Id" = {fireId}
              AND "Outcome" = 'claimed'
              AND "DispatchedAt" IS NULL;
            """, ct);
        return affected == 1;
    }

    public async Task<int> PruneLedgerAsync(
        DateTime olderThanUtc, int maxRows = 1000, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Bounded DELETE (D2 retention): subquery + LIMIT keeps one tick's
        // prune cheap; the next tick takes the next slice.
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM scheduled_trigger_fires
            WHERE "Id" IN (
                SELECT "Id" FROM scheduled_trigger_fires
                WHERE "ClaimedAt" < {olderThanUtc}
                ORDER BY "ClaimedAt"
                LIMIT {maxRows});
            """, ct);
    }
}
