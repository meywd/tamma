using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// CRUD repository for <see cref="ProviderHealth"/>. Circuit-breaker logic
/// lives in <c>CircuitBreakerService</c>; this class only reads/writes
/// persistent rows.
///
/// <para>Epic 28 note: provider health has tenant-scoped rows
/// (<c>TenantId = &lt;guid&gt;</c>) and a platform-default row
/// (<c>TenantId = NULL</c>). During the transition (shared physical DB)
/// the repo uses a single long-lived context per request so the
/// <c>GetOrCreate → mutate → SaveChanges</c> pattern preserved in
/// <c>CircuitBreakerService</c> keeps its EF-tracked entity across
/// method calls. The context is chosen lazily:
/// <see cref="ITenantDbContextFactory"/> when the ambient tenant is set,
/// else <see cref="ControlPlaneDbContext"/>.</para>
/// </summary>
public class ProviderHealthRepository : IProviderHealthRepository, IAsyncDisposable
{
    private readonly ITenantDbContextFactory _factory;
    private readonly ITenantContext _tenantContext;
    private readonly ControlPlaneDbContext _cp;
    private TenantDbContext? _tenantContextDb;

    public ProviderHealthRepository(
        ITenantDbContextFactory factory,
        ITenantContext tenantContext,
        ControlPlaneDbContext cp)
    {
        _factory = factory;
        _tenantContext = tenantContext;
        _cp = cp;
    }

    private async Task<DbContext> GetContextAsync()
    {
        if (_tenantContext.TenantId is Guid tid)
        {
            _tenantContextDb ??= await _factory.CreateAsync(tid);
            return _tenantContextDb;
        }
        return _cp;
    }

    private Microsoft.EntityFrameworkCore.DbSet<ProviderHealth> GetSet(DbContext ctx) =>
        ctx is TenantDbContext t ? t.ProviderHealths : ((ControlPlaneDbContext)ctx).ProviderHealths;

    public async Task<ProviderHealth?> GetStatusAsync(string providerKey, Guid? tenantId)
    {
        var ctx = await GetContextAsync();
        return await GetSet(ctx).IgnoreQueryFilters()
            .FirstOrDefaultAsync(h => h.ProviderKey == providerKey && h.TenantId == tenantId);
    }

    public async Task<List<ProviderHealth>> GetAllAsync(Guid? tenantId)
    {
        var ctx = await GetContextAsync();
        return await GetSet(ctx).IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId).ToListAsync();
    }

    public async Task<ProviderHealth> GetOrCreateAsync(string providerKey, Guid? tenantId)
    {
        var ctx = await GetContextAsync();
        var set = GetSet(ctx);
        var health = await set.IgnoreQueryFilters()
            .FirstOrDefaultAsync(h => h.ProviderKey == providerKey && h.TenantId == tenantId);
        if (health is not null) return health;

        health = new ProviderHealth
        {
            ProviderKey = providerKey,
            TenantId = tenantId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        set.Add(health);
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
