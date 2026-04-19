using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class PromptRepository(TammaDbContext db) : IPromptRepository
{
    public async Task<PromptOverride?> GetAsync(Guid? userId, string scope, string? role, string? action)
        => await db.PromptOverrides
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Scope == scope && p.Role == role && p.Action == action);

    public async Task<(PromptOverride Entity, bool WasCreated)> UpsertAsync(PromptOverride prompt, Guid? actingUserId = null)
    {
        var existing = await db.PromptOverrides
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
            // Bump optimistic-concurrency version (audit prompts/010) and
            // record who edited (defaults to the row's UserId for self-edit
            // when no separate acting user is provided).
            existing.Version += 1;
            existing.UpdatedBy = actingUserId ?? prompt.UserId;
            await db.SaveChangesAsync();
            return (existing, false);
        }
        prompt.CreatedAt = DateTime.UtcNow;
        prompt.UpdatedAt = DateTime.UtcNow;
        prompt.Version = 1;
        prompt.CreatedBy = actingUserId ?? prompt.UserId;
        prompt.UpdatedBy = actingUserId ?? prompt.UserId;
        db.PromptOverrides.Add(prompt);
        await db.SaveChangesAsync();
        return (prompt, true);
    }

    public async Task<bool> DeleteAsync(Guid? userId, string scope, string? role, string? action)
    {
        var prompt = await db.PromptOverrides
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Scope == scope && p.Role == role && p.Action == action);
        if (prompt is null) return false;
        db.PromptOverrides.Remove(prompt);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<PromptOverride>> ListAsync(Guid? userId)
        => await db.PromptOverrides.Where(p => p.UserId == userId).ToListAsync();
}
