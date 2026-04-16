using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class TenantMembershipRepository(TammaDbContext db) : ITenantMembershipRepository
{
    public async Task<TenantMembership> AddAsync(Guid tenantId, Guid userId, string role)
    {
        var membership = new TenantMembership
        {
            TenantId = tenantId,
            UserId = userId,
            Role = role,
            JoinedAt = DateTime.UtcNow
        };
        db.TenantMemberships.Add(membership);
        await db.SaveChangesAsync();
        return membership;
    }

    public async Task RemoveAsync(Guid tenantId, Guid userId)
    {
        var membership = await db.TenantMemberships
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId);
        if (membership is not null)
        {
            db.TenantMemberships.Remove(membership);
            await db.SaveChangesAsync();
        }
    }

    public async Task<string?> GetRoleAsync(Guid tenantId, Guid userId)
    {
        var membership = await db.TenantMemberships
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId);
        return membership?.Role;
    }

    public async Task<(List<TenantMembership> Members, int Total)> ListByTenantAsync(Guid tenantId, int limit, int offset)
    {
        var query = db.TenantMemberships.Where(m => m.TenantId == tenantId).Include(m => m.User);
        var total = await query.CountAsync();
        var members = await query.OrderBy(m => m.JoinedAt).Skip(offset).Take(limit).ToListAsync();
        return (members, total);
    }

    public async Task<List<TenantMembership>> GetUserTenantsAsync(Guid userId)
        => await db.TenantMemberships.Where(m => m.UserId == userId).Include(m => m.Tenant).ToListAsync();

    public async Task UpdateRoleAsync(Guid tenantId, Guid userId, string role)
    {
        var membership = await db.TenantMemberships
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId);
        if (membership is not null)
        {
            membership.Role = role;
            await db.SaveChangesAsync();
        }
    }
}
