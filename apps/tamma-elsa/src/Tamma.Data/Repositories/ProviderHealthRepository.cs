using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class ProviderHealthRepository(TammaDbContext db) : IProviderHealthRepository
{
    public async Task RecordSuccessAsync(string providerKey, Guid? tenantId)
    {
        var health = await GetOrCreateAsync(providerKey, tenantId);
        health.Status = "healthy";
        health.LastSuccess = DateTime.UtcNow;
        health.FailureCount = 0;
        health.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task RecordFailureAsync(string providerKey, Guid? tenantId)
    {
        var health = await GetOrCreateAsync(providerKey, tenantId);
        health.FailureCount++;
        health.LastFailure = DateTime.UtcNow;
        health.Status = health.FailureCount >= 5 ? "down" : "degraded";
        health.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task<ProviderHealth?> GetStatusAsync(string providerKey, Guid? tenantId)
        => await db.ProviderHealths.IgnoreQueryFilters()
            .FirstOrDefaultAsync(h => h.ProviderKey == providerKey && h.TenantId == tenantId);

    public async Task<List<ProviderHealth>> GetAllAsync(Guid? tenantId)
        => await db.ProviderHealths.IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId).ToListAsync();

    public async Task ResetAsync(string providerKey, Guid? tenantId)
    {
        var health = await db.ProviderHealths.IgnoreQueryFilters()
            .FirstOrDefaultAsync(h => h.ProviderKey == providerKey && h.TenantId == tenantId);
        if (health is not null)
        {
            health.Status = "unknown";
            health.FailureCount = 0;
            health.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    private async Task<ProviderHealth> GetOrCreateAsync(string providerKey, Guid? tenantId)
    {
        var health = await db.ProviderHealths.IgnoreQueryFilters()
            .FirstOrDefaultAsync(h => h.ProviderKey == providerKey && h.TenantId == tenantId);
        if (health is not null) return health;

        health = new ProviderHealth
        {
            ProviderKey = providerKey,
            TenantId = tenantId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.ProviderHealths.Add(health);
        return health;
    }
}
