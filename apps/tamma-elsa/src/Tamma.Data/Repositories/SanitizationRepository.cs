using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class SanitizationRepository(TammaDbContext db) : ISanitizationRepository
{
    public async Task<SanitizationRule?> GetRulesAsync(Guid? tenantId)
        => await db.SanitizationRules.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId);

    public async Task<SanitizationRule> UpsertRulesAsync(Guid? tenantId, string rulesJson)
    {
        var existing = await db.SanitizationRules.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId);
        if (existing is not null)
        {
            existing.Rules = rulesJson;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return existing;
        }
        var rule = new SanitizationRule
        {
            TenantId = tenantId,
            Rules = rulesJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.SanitizationRules.Add(rule);
        await db.SaveChangesAsync();
        return rule;
    }
}
