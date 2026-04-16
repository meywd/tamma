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
