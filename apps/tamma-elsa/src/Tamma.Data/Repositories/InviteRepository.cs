using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class InviteRepository(TammaDbContext db) : IInviteRepository
{
    public async Task<UserInvite> CreateAsync(UserInvite invite)
    {
        invite.CreatedAt = DateTime.UtcNow;
        db.UserInvites.Add(invite);
        await db.SaveChangesAsync();
        return invite;
    }

    public async Task<UserInvite?> GetByIdAsync(Guid id)
        => await db.UserInvites.FirstOrDefaultAsync(i => i.Id == id);

    public async Task<UserInvite?> GetByTokenHashAsync(string tokenHash)
        => await db.UserInvites.FirstOrDefaultAsync(i => i.InviteTokenHash == tokenHash);

    public async Task AcceptAsync(Guid id)
    {
        var invite = await db.UserInvites.FindAsync(id);
        if (invite is not null)
        {
            invite.AcceptedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task<List<UserInvite>> ListPendingByTenantAsync(Guid tenantId)
        => await db.UserInvites
            .Where(i => i.TenantId == tenantId && i.AcceptedAt == null && i.ExpiresAt > DateTime.UtcNow)
            .ToListAsync();

    public async Task<bool> DeleteScopedAsync(Guid tenantId, Guid id)
    {
        var invite = await db.UserInvites
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId);
        if (invite is null) return false;
        db.UserInvites.Remove(invite);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<int> DeleteAllByTenantAsync(Guid tenantId)
    {
        var rows = await db.UserInvites.Where(i => i.TenantId == tenantId).ToListAsync();
        if (rows.Count == 0) return 0;
        db.UserInvites.RemoveRange(rows);
        await db.SaveChangesAsync();
        return rows.Count;
    }

    public async Task<UserInvite?> GetByIdScopedAsync(Guid tenantId, Guid id)
        => await db.UserInvites
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId);

    public async Task ExtendExpiryAsync(Guid id, DateTime newExpiresAt)
    {
        var invite = await db.UserInvites.FindAsync(id);
        if (invite is null) return;
        invite.ExpiresAt = newExpiresAt;
        await db.SaveChangesAsync();
    }

    [Obsolete("Use DeleteScopedAsync for per-tenant invariant. Kept for transitional callers.")]
    public async Task DeleteAsync(Guid id)
    {
        var invite = await db.UserInvites.FindAsync(id);
        if (invite is not null)
        {
            db.UserInvites.Remove(invite);
            await db.SaveChangesAsync();
        }
    }
}
