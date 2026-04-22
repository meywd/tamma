using Tamma.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Tenant-scoped prompt overrides. Uses <see cref="ITenantDbContextFactory"/>
/// when the ambient <see cref="ITenantContext"/> carries a tenant id;
/// otherwise falls back to <see cref="ControlPlaneDbContext"/> for
/// cross-user lookup paths (system scope, migrations).
/// </summary>
public class PromptRepository(
    ITenantDbContextFactory tenantDbFactory,
    ITenantContext tenantContext,
    ControlPlaneDbContext cp) : IPromptRepository
{
    public async Task<PromptOverride?> GetAsync(Guid? userId, string scope, string? role, string? action)
    {
        if (tenantContext.TenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            return await db.PromptOverrides.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.UserId == userId && p.Scope == scope && p.Role == role && p.Action == action);
        }
        return await cp.PromptOverrides.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Scope == scope && p.Role == role && p.Action == action);
    }

    public async Task<(PromptOverride Entity, bool WasCreated)> UpsertAsync(
        PromptOverride prompt, Guid? actingUserId = null)
    {
        if (tenantContext.TenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            return await UpsertInternal(db.PromptOverrides, () => db.SaveChangesAsync(), prompt, actingUserId);
        }
        return await UpsertInternal(cp.PromptOverrides, () => cp.SaveChangesAsync(), prompt, actingUserId);
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
        if (tenantContext.TenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            var prompt = await db.PromptOverrides.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.UserId == userId && p.Scope == scope && p.Role == role && p.Action == action);
            if (prompt is null) return false;
            db.PromptOverrides.Remove(prompt);
            await db.SaveChangesAsync();
            return true;
        }
        var row = await cp.PromptOverrides.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Scope == scope && p.Role == role && p.Action == action);
        if (row is null) return false;
        cp.PromptOverrides.Remove(row);
        await cp.SaveChangesAsync();
        return true;
    }

    public async Task<List<PromptOverride>> ListAsync(Guid? userId)
    {
        if (tenantContext.TenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            return await db.PromptOverrides.IgnoreQueryFilters()
                .Where(p => p.UserId == userId).ToListAsync();
        }
        return await cp.PromptOverrides.IgnoreQueryFilters()
            .Where(p => p.UserId == userId).ToListAsync();
    }
}
