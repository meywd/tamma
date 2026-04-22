using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class RefreshTokenRepository(ControlPlaneDbContext db) : IRefreshTokenRepository
{
    public async Task<RefreshToken> CreateAsync(Guid userId, string tokenHash, DateTime expiresAt)
    {
        var token = new RefreshToken
        {
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow
        };
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync();
        return token;
    }

    public async Task<RefreshToken?> GetByTokenHashAsync(string tokenHash)
        => await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

    public async Task RevokeAsync(Guid id)
    {
        var token = await db.RefreshTokens.FindAsync(id);
        if (token is not null)
        {
            token.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task RevokeAllForUserAsync(Guid userId)
    {
        var tokens = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync();
        foreach (var token in tokens)
            token.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task<int> CleanExpiredAsync()
    {
        var expired = await db.RefreshTokens
            .Where(t => t.ExpiresAt < DateTime.UtcNow || t.RevokedAt != null)
            .ToListAsync();
        db.RefreshTokens.RemoveRange(expired);
        await db.SaveChangesAsync();
        return expired.Count;
    }
}
