using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// CRUD repository for <see cref="ProviderHealth"/>. All circuit-breaker
/// state-machine behaviour lives in <c>CircuitBreakerService</c>; this class
/// only reads/writes persistent rows.
/// </summary>
// Story 19-6: per-request circuit-breaker state binds to TammaAppDbContext
// so RLS + fail-closed EF filter both fire. IgnoreQueryFilters() is still
// honoured for the per-tenant lookups below — those queries explicitly
// constrain on TenantId already, but they bypass the global filter so the
// platform-default (TenantId == null) row is reachable.
public class ProviderHealthRepository(TammaAppDbContext db) : IProviderHealthRepository
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
