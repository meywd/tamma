using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class TenantRepository(TammaDbContext db) : ITenantRepository
{
    public async Task<Tenant> CreateAsync(Tenant tenant)
    {
        tenant.CreatedAt = DateTime.UtcNow;
        tenant.UpdatedAt = DateTime.UtcNow;
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    public async Task<Tenant?> GetByIdAsync(Guid id)
        => await db.Tenants.FirstOrDefaultAsync(t => t.Id == id);

    public async Task<Tenant?> GetBySlugAsync(string slug)
        => await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug);

    public async Task<Tenant?> GetByExternalIdAsync(string externalId)
        => await db.Tenants.FirstOrDefaultAsync(t => t.ExternalId == externalId);

    public async Task<Tenant> UpdateAsync(Tenant tenant)
    {
        tenant.UpdatedAt = DateTime.UtcNow;
        db.Tenants.Update(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    public async Task SoftDeleteAsync(Guid id)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id);
        if (tenant is not null)
        {
            tenant.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task<List<Tenant>> ListByUserAsync(Guid userId)
    {
        return await db.TenantMemberships
            .Where(m => m.UserId == userId)
            .Include(m => m.Tenant)
            .Select(m => m.Tenant)
            .ToListAsync();
    }

    public async Task<List<TenantMembershipView>> ListMembershipsByUserAsync(Guid userId)
    {
        var rows = await db.TenantMemberships
            .Where(m => m.UserId == userId)
            .Include(m => m.Tenant)
            .OrderBy(m => m.JoinedAt)
            .Select(m => new { m.Tenant, m.Role, m.JoinedAt })
            .ToListAsync();
        return rows.Select(r => new TenantMembershipView(r.Tenant, r.Role, r.JoinedAt))
            .ToList();
    }
}
