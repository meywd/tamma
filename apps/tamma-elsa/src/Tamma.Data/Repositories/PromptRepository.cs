using Tamma.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Tenant-scoped prompt overrides. Uses <see cref="ITenantDbContextFactory"/>
/// for all reads/writes; the ambient <see cref="ITenantContext"/> MUST carry
/// a tenant id. System defaults moved to code (PR A, Decision #1) so the
/// tenant-less code path is no longer reachable.
///
/// <para>Story 28-1 PR D: prompt_overrides table moved off
/// <see cref="ControlPlaneDbContext"/>; the CP fallback for system-scope
/// lookups is gone. Callers without a tenant id receive an
/// <see cref="InvalidOperationException"/> at the seam.</para>
///
/// <para>Story 27-2: dual scoping model. Single-user-mode rows have
/// <c>user_id</c> set, <c>tenant_id IS NULL</c>; SaaS-mode rows have
/// <c>tenant_id</c> set, <c>user_id IS NULL</c>. The DB
/// <c>principal_xor</c> CHECK constraint forces exactly-one. The
/// methods are parallel — pick the right one based on the caller's mode;
/// no method silently joins both planes.</para>
/// </summary>
public class PromptRepository(
    ITenantDbContextFactory tenantDbFactory,
    ITenantContext tenantContext) : IPromptRepository
{
    private Guid RequireTenantId() => tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "PromptRepository requires an ambient tenant id. Story 28-1 PR D " +
            "moved prompt_overrides off the control plane; system-default " +
            "lookups now resolve from in-code defaults via PromptStore, not " +
            "from CP rows.");

    // ───────────────────────── single-user mode ─────────────────────────

    public async Task<PromptOverride?> GetAsync(Guid? userId, string scope, string? role, string? action)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        // tenant_id IS NULL discriminator keeps this query off SaaS-mode rows.
        return await db.PromptOverrides.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.TenantId == default(Guid?)
                && p.Scope == scope && p.Role == role && p.Action == action);
    }

    public async Task<(PromptOverride Entity, bool WasCreated)> UpsertAsync(
        PromptOverride prompt, Guid? actingUserId = null)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await UpsertInternal(db.PromptOverrides, () => db.SaveChangesAsync(), prompt, actingUserId);
    }

    private static async Task<(PromptOverride Entity, bool WasCreated)> UpsertInternal(
        Microsoft.EntityFrameworkCore.DbSet<PromptOverride> set,
        Func<Task<int>> save,
        PromptOverride prompt,
        Guid? actingUserId)
    {
        // Story 27-2 — match on BOTH user_id AND tenant_id so single-user
        // and SaaS rows for the same (scope, role, action) tuple don't
        // collide. The principal_xor CHECK guarantees exactly one of the
        // two predicates picks the existing row.
        var existing = await set.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p =>
                p.UserId == prompt.UserId && p.TenantId == prompt.TenantId &&
                p.Scope == prompt.Scope &&
                p.Role == prompt.Role && p.Action == prompt.Action);
        if (existing is not null)
        {
            existing.Template = prompt.Template;
            existing.SystemPrompt = prompt.SystemPrompt;
            existing.Variables = prompt.Variables;
            existing.EnableTools = prompt.EnableTools;
            existing.MaxTokens = prompt.MaxTokens;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.Version += 1;
            existing.UpdatedBy = actingUserId ?? prompt.UserId;
            await save();
            return (existing, false);
        }
        prompt.CreatedAt = DateTime.UtcNow;
        prompt.UpdatedAt = DateTime.UtcNow;
        prompt.Version = 1;
        prompt.CreatedBy = actingUserId ?? prompt.UserId;
        prompt.UpdatedBy = actingUserId ?? prompt.UserId;
        set.Add(prompt);
        await save();
        return (prompt, true);
    }

    public async Task<bool> DeleteAsync(Guid? userId, string scope, string? role, string? action)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var prompt = await db.PromptOverrides.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.TenantId == default(Guid?)
                && p.Scope == scope && p.Role == role && p.Action == action);
        if (prompt is null) return false;
        db.PromptOverrides.Remove(prompt);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<PromptOverride>> ListAsync(Guid? userId)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        // tenant_id IS NULL keeps SaaS-mode rows out of the user list.
        return await db.PromptOverrides.IgnoreQueryFilters()
            .Where(p => p.UserId == userId && p.TenantId == default(Guid?)).ToListAsync();
    }

    // ───────────────────────── SaaS mode (Story 27-2) ───────────────────

    public async Task<PromptOverride?> GetByTenantAsync(
        Guid tenantId, string scope, string? role, string? action)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        // PromptRepository's ambient tenant id should match the requested
        // tenant — the factory routes the connection. Use the scoped
        // tenant id to pick the right physical DB.
        var ambient = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(ambient);
        // user_id IS NULL discriminator excludes single-user-mode rows.
        return await db.PromptOverrides.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.UserId == default(Guid?)
                && p.Scope == scope && p.Role == role && p.Action == action);
    }

    public async Task<bool> DeleteByTenantAsync(
        Guid tenantId, string scope, string? role, string? action)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        var ambient = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(ambient);
        var prompt = await db.PromptOverrides.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.UserId == default(Guid?)
                && p.Scope == scope && p.Role == role && p.Action == action);
        if (prompt is null) return false;
        db.PromptOverrides.Remove(prompt);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<PromptOverride>> ListByTenantAsync(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        var ambient = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(ambient);
        return await db.PromptOverrides.IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.UserId == default(Guid?)).ToListAsync();
    }
}
