using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class PasswordResetRepository(TammaDbContext db) : IPasswordResetRepository
{
    public async Task<PasswordResetToken> CreateAsync(Guid userId, string tokenHash, DateTime expiresAt)
    {
        var token = new PasswordResetToken
        {
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow
        };
        db.PasswordResetTokens.Add(token);
        await db.SaveChangesAsync();
        return token;
    }

    public async Task<PasswordResetToken?> GetByTokenHashAsync(string tokenHash)
        => await db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

    public async Task ConsumeAsync(Guid id)
    {
        var token = await db.PasswordResetTokens.FindAsync(id);
        if (token is not null)
        {
            token.ConsumedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task<int> CleanExpiredAsync()
    {
        var expired = await db.PasswordResetTokens
            .Where(t => t.ExpiresAt < DateTime.UtcNow || t.ConsumedAt != null)
            .ToListAsync();
        db.PasswordResetTokens.RemoveRange(expired);
        await db.SaveChangesAsync();
        return expired.Count;
    }
}
