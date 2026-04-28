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

    public async Task<PromptOverride?> GetAsync(Guid? userId, string scope, string? role, string? action)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await db.PromptOverrides.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Scope == scope && p.Role == role && p.Action == action);
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
        var existing = await set.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p =>
                p.UserId == prompt.UserId && p.Scope == prompt.Scope &&
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
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Scope == scope && p.Role == role && p.Action == action);
        if (prompt is null) return false;
        db.PromptOverrides.Remove(prompt);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<PromptOverride>> ListAsync(Guid? userId)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await db.PromptOverrides.IgnoreQueryFilters()
            .Where(p => p.UserId == userId).ToListAsync();
    }
}
