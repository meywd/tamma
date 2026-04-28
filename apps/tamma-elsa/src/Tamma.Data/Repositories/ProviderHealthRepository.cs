using Tamma.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// CRUD repository for <see cref="ProviderHealth"/>. Circuit-breaker logic
/// lives in <c>CircuitBreakerService</c>; this class only reads/writes
/// persistent rows.
///
/// <para>Story 28-1 PR D: provider_health moved off
/// <see cref="ControlPlaneDbContext"/>. Every operation now requires an
/// ambient tenant id; platform-default rows are gone (PR A, Decision #1
/// — defaults live in code). The repo uses a single long-lived context
/// per request so the <c>GetOrCreate → mutate → SaveChanges</c> pattern
/// in <c>CircuitBreakerService</c> keeps its EF-tracked entity across
/// method calls.</para>
/// </summary>
public class ProviderHealthRepository : IProviderHealthRepository, IAsyncDisposable
{
    private readonly ITenantDbContextFactory _factory;
    private readonly ITenantContext _tenantContext;
    private TenantDbContext? _tenantContextDb;

    public ProviderHealthRepository(
        ITenantDbContextFactory factory,
        ITenantContext tenantContext)
    {
        _factory = factory;
        _tenantContext = tenantContext;
    }

    private Guid RequireTenantId() => _tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "ProviderHealthRepository requires an ambient tenant id. Story " +
            "28-1 PR D moved provider_health off the control plane; platform-" +
            "default health rows live in code (DefaultProviderHealth) per PR A.");

    private async Task<TenantDbContext> GetContextAsync()
    {
        var tid = RequireTenantId();
        _tenantContextDb ??= await _factory.CreateAsync(tid);
        return _tenantContextDb;
    }

    public async Task<ProviderHealth?> GetStatusAsync(string providerKey, Guid? tenantId)
    {
        var ctx = await GetContextAsync();
        return await ctx.ProviderHealths.IgnoreQueryFilters()
            .FirstOrDefaultAsync(h => h.ProviderKey == providerKey && h.TenantId == tenantId);
    }

    public async Task<List<ProviderHealth>> GetAllAsync(Guid? tenantId)
    {
        var ctx = await GetContextAsync();
        return await ctx.ProviderHealths.IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId).ToListAsync();
    }

    public async Task<ProviderHealth> GetOrCreateAsync(string providerKey, Guid? tenantId)
    {
        var ctx = await GetContextAsync();
        var health = await ctx.ProviderHealths.IgnoreQueryFilters()
            .FirstOrDefaultAsync(h => h.ProviderKey == providerKey && h.TenantId == tenantId);
        if (health is not null) return health;

        health = new ProviderHealth
        {
            ProviderKey = providerKey,
            TenantId = tenantId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.ProviderHealths.Add(health);
        return health;
    }

    public async Task SaveChangesAsync()
    {
        var ctx = await GetContextAsync();
        await ctx.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_tenantContextDb is not null)
        {
            await _tenantContextDb.DisposeAsync();
            _tenantContextDb = null;
        }
    }
}
