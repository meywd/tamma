using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// CRUD repository for <see cref="ProviderHealth"/>. All circuit-breaker
/// state-machine behaviour lives in <c>CircuitBreakerService</c>; this class
/// only reads/writes persistent rows.
/// </summary>
public class ProviderHealthRepository(TammaDbContext db) : IProviderHealthRepository
{
    public async Task<ProviderHealth?> GetStatusAsync(string providerKey, Guid? tenantId)
        => await db.ProviderHealths.IgnoreQueryFilters()
            .FirstOrDefaultAsync(h => h.ProviderKey == providerKey && h.TenantId == tenantId);

    public async Task<List<ProviderHealth>> GetAllAsync(Guid? tenantId)
        => await db.ProviderHealths.IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId).ToListAsync();

    public async Task<ProviderHealth> GetOrCreateAsync(string providerKey, Guid? tenantId)
    {
        var health = await db.ProviderHealths.IgnoreQueryFilters()
            .FirstOrDefaultAsync(h => h.ProviderKey == providerKey && h.TenantId == tenantId);
        if (health is not null) return health;

        health = new ProviderHealth
        {
            ProviderKey = providerKey,
            TenantId = tenantId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.ProviderHealths.Add(health);
        return health;
    }

    public Task SaveChangesAsync() => db.SaveChangesAsync();
}
