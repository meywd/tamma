using Microsoft.EntityFrameworkCore;
using Tamma.Core.Tracking;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Tenant-scoped tracker preferences (Story 44-1 AC6). Mirrors
/// <see cref="AcceptanceRulesRepository"/> exactly: all reads/writes go through
/// <see cref="ITenantDbContextFactory"/> with the ambient
/// <see cref="ITenantContext"/> tenant id. Single-user rows carry
/// <c>user_id</c> (tenant_id NULL); SaaS rows carry <c>tenant_id</c> (user_id
/// NULL); the STRONG <c>principal_xor</c> CHECK forces exactly-one. The two
/// surfaces are PARALLEL — every predicate pins the opposite key to NULL, so
/// no method can silently join both planes.
/// </summary>
public class TrackerPreferenceRepository(
    ITenantDbContextFactory tenantDbFactory,
    ITenantContext tenantContext) : ITrackerPreferenceRepository
{
    private Guid RequireTenantId() => tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "TrackerPreferenceRepository requires an ambient tenant id. "
            + "tracker_preferences is tenant-resident (mirrors acceptance_rules_overrides).");

    // ───────────────────────── single-user mode ─────────────────────────

    public async Task<TrackerPreference?> GetAsync(Guid? userId)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await db.TrackerPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.TenantId == default(Guid?));
    }

    public async Task<(TrackerPreference Entity, bool WasCreated)> UpsertAsync(
        TrackerPreference preference, Guid? actingUserId = null)
    {
        ArgumentNullException.ThrowIfNull(preference);
        if (preference.UserId is null || preference.TenantId is not null)
            throw new ArgumentException(
                "UpsertAsync is the single-user surface: UserId must be set and TenantId null. "
                + "Use UpsertForTenantAsync for the tenant plane.", nameof(preference));

        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await UpsertInternal(db, preference, actingUserId);
    }

    public async Task<bool> DeleteAsync(Guid? userId)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var row = await db.TrackerPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.TenantId == default(Guid?));
        if (row is null)
            return false;
        db.TrackerPreferences.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    // ───────────────────────── SaaS mode ────────────────────────────────

    public async Task<TrackerPreference?> GetByTenantAsync(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        var ambient = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(ambient);
        return await db.TrackerPreferences
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.UserId == default(Guid?));
    }

    public async Task<(TrackerPreference Entity, bool WasCreated)> UpsertForTenantAsync(
        TrackerPreference preference, Guid? actingUserId = null)
    {
        ArgumentNullException.ThrowIfNull(preference);
        if (preference.TenantId is null || preference.UserId is not null)
            throw new ArgumentException(
                "UpsertForTenantAsync is the SaaS surface: TenantId must be set and UserId null. "
                + "Use UpsertAsync for the user plane.", nameof(preference));

        var ambient = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(ambient);
        return await UpsertInternal(db, preference, actingUserId);
    }

    public async Task<bool> DeleteByTenantAsync(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        var ambient = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(ambient);
        var row = await db.TrackerPreferences
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.UserId == default(Guid?));
        if (row is null)
            return false;
        db.TrackerPreferences.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    // Match on BOTH keys so the two planes cannot collide; the principal_xor
    // CHECK guarantees exactly one predicate picks the existing row (the
    // AcceptanceRulesRepository.UpsertInternal shape).
    private static async Task<(TrackerPreference Entity, bool WasCreated)> UpsertInternal(
        TenantDbContext db, TrackerPreference preference, Guid? actingUserId)
    {
        // Wire-boundary validation (the "typed TammaError, never a DB CHECK
        // surprise" posture): DefaultKind must be a WorkItemKind wire string
        // or null — otherwise junk reaches Postgres and surfaces as a raw
        // 23514 off ck_tracker_preferences_default_kind. BoardGroupBy stays
        // freeform by design.
        if (preference.DefaultKind is not null)
            _ = WorkItemKindExtensions.Parse(preference.DefaultKind); // TRACKER.UNKNOWN_KIND

        var existing = await db.TrackerPreferences.FirstOrDefaultAsync(p =>
            p.UserId == preference.UserId && p.TenantId == preference.TenantId);
        if (existing is not null)
            return (await ApplyUpdateAsync(db, existing, preference, actingUserId), false);

        preference.CreatedAt = DateTime.UtcNow;
        preference.UpdatedAt = preference.CreatedAt;
        preference.Version = 1;
        preference.CreatedBy = actingUserId ?? preference.UserId;
        preference.UpdatedBy = actingUserId ?? preference.UserId;
        db.TrackerPreferences.Add(preference);
        try
        {
            await db.SaveChangesAsync();
            return (preference, true);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Two concurrent FIRST upserts for the same principal raced the
            // check-then-insert window; the loser hit
            // UX_tracker_preferences_principal. Retry ONCE as an update of the
            // winner's row — the documented upsert semantics, idempotent under
            // the race. The row can no longer vanish mid-retry without a
            // delete, in which case First throws and the caller sees a real
            // failure, not a silent swallow.
            db.Entry(preference).State = EntityState.Detached;
            var winner = await db.TrackerPreferences.FirstAsync(p =>
                p.UserId == preference.UserId && p.TenantId == preference.TenantId);
            return (await ApplyUpdateAsync(db, winner, preference, actingUserId), false);
        }
    }

    private static async Task<TrackerPreference> ApplyUpdateAsync(
        TenantDbContext db, TrackerPreference existing, TrackerPreference incoming, Guid? actingUserId)
    {
        existing.DefaultProjectId = incoming.DefaultProjectId;
        existing.DefaultKind = incoming.DefaultKind;
        existing.BoardGroupBy = incoming.BoardGroupBy;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.Version += 1;
        existing.UpdatedBy = actingUserId ?? incoming.UserId;
        await db.SaveChangesAsync();
        return existing;
    }
}
