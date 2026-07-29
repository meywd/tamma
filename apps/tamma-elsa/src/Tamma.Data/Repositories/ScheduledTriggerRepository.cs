using Microsoft.EntityFrameworkCore;
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
    IDbContextFactory<ControlPlaneDbContext> dbFactory) : IScheduledTriggerRepository
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
        var rows = await db.ScheduledTriggerFires
            .AsNoTracking()
            .Where(f => f.Outcome == "claimed"
                && f.DispatchedAt == null
                && f.WindowKey.StartsWith("manual:"))
            .OrderBy(f => f.ClaimedAt)
            .Take(limit)
            .Join(
                db.ScheduledTriggers.AsNoTracking(),
                f => f.TriggerId,
                t => t.Id,
                (f, t) => new { Fire = f, Trigger = t })
            .ToListAsync(ct);

        return rows.Select(r => (r.Fire, r.Trigger)).ToList();
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
