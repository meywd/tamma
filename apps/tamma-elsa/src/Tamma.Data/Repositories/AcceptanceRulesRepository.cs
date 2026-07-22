using Microsoft.EntityFrameworkCore;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Tenant-scoped acceptance-rules overrides (Story 39-5). Mirrors
/// <see cref="PromptRepository"/> exactly: uses <see cref="ITenantDbContextFactory"/>
/// for all reads/writes; the ambient <see cref="ITenantContext"/> MUST carry a
/// tenant id (single-user users own a personal tenant DB, so both modes land in
/// the same physical home). Single-user rows carry <c>user_id</c> (tenant_id
/// NULL); SaaS rows carry <c>tenant_id</c> (user_id NULL); the DB
/// <c>principal_xor</c> CHECK forces exactly-one. <c>documentTypeKey</c> NULL
/// addresses the principal base row.
/// </summary>
public class AcceptanceRulesRepository(
    ITenantDbContextFactory tenantDbFactory,
    ITenantContext tenantContext) : IAcceptanceRulesRepository
{
    private Guid RequireTenantId() => tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "AcceptanceRulesRepository requires an ambient tenant id. acceptance_rules_overrides " +
            "is tenant-resident (mirrors prompt_overrides); system defaults resolve from in-code " +
            "AcceptanceDefaults via AcceptanceRulesService, not from DB rows.");

    // ───────────────────────── single-user mode ─────────────────────────

    public async Task<AcceptanceRulesOverride?> GetAsync(Guid? userId, string? documentTypeKey)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await db.AcceptanceRulesOverrides.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.TenantId == default(Guid?)
                && p.DocumentTypeKey == documentTypeKey);
    }

    public async Task<(AcceptanceRulesOverride Entity, bool WasCreated)> UpsertAsync(
        AcceptanceRulesOverride entity, Guid? actingUserId = null)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await UpsertInternal(db.AcceptanceRulesOverrides, () => db.SaveChangesAsync(), entity, actingUserId);
    }

    private static async Task<(AcceptanceRulesOverride Entity, bool WasCreated)> UpsertInternal(
        DbSet<AcceptanceRulesOverride> set,
        Func<Task<int>> save,
        AcceptanceRulesOverride entity,
        Guid? actingUserId)
    {
        // Match on BOTH user_id AND tenant_id AND the type key so single-user
        // and SaaS rows for the same key don't collide. The principal_xor CHECK
        // guarantees exactly one predicate picks the existing row.
        var existing = await set.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p =>
                p.UserId == entity.UserId && p.TenantId == entity.TenantId &&
                p.DocumentTypeKey == entity.DocumentTypeKey);
        if (existing is not null)
        {
            existing.RulesJson = entity.RulesJson;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.Version += 1;
            existing.UpdatedBy = actingUserId ?? entity.UserId;
            await save();
            return (existing, false);
        }
        entity.CreatedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;
        entity.Version = 1;
        entity.CreatedBy = actingUserId ?? entity.UserId;
        entity.UpdatedBy = actingUserId ?? entity.UserId;
        set.Add(entity);
        await save();
        return (entity, true);
    }

    public async Task<bool> DeleteAsync(Guid? userId, string? documentTypeKey)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var row = await db.AcceptanceRulesOverrides.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.TenantId == default(Guid?)
                && p.DocumentTypeKey == documentTypeKey);
        if (row is null) return false;
        db.AcceptanceRulesOverrides.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<AcceptanceRulesOverride>> ListAsync(Guid? userId)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await db.AcceptanceRulesOverrides.IgnoreQueryFilters()
            .Where(p => p.UserId == userId && p.TenantId == default(Guid?)).ToListAsync();
    }

    // ───────────────────────── SaaS mode ────────────────────────────────

    public async Task<AcceptanceRulesOverride?> GetByTenantAsync(Guid tenantId, string? documentTypeKey)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        var ambient = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(ambient);
        return await db.AcceptanceRulesOverrides.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.UserId == default(Guid?)
                && p.DocumentTypeKey == documentTypeKey);
    }

    public async Task<bool> DeleteByTenantAsync(Guid tenantId, string? documentTypeKey)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        var ambient = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(ambient);
        var row = await db.AcceptanceRulesOverrides.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.UserId == default(Guid?)
                && p.DocumentTypeKey == documentTypeKey);
        if (row is null) return false;
        db.AcceptanceRulesOverrides.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<AcceptanceRulesOverride>> ListByTenantAsync(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        var ambient = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(ambient);
        return await db.AcceptanceRulesOverrides.IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.UserId == default(Guid?)).ToListAsync();
    }
}
