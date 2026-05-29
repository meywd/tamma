using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class RefreshTokenRepository(ControlPlaneDbContext db) : IRefreshTokenRepository
{
    public Task<RefreshToken> CreateAsync(Guid userId, string tokenHash, DateTime expiresAt)
        => CreateAsync(userId, tenantId: null, tokenHash, expiresAt, jtiChainHead: null);

    public async Task<RefreshToken> CreateAsync(
        Guid userId,
        Guid? tenantId,
        string tokenHash,
        DateTime expiresAt,
        Guid? jtiChainHead)
    {
        var token = new RefreshToken
        {
            UserId = userId,
            TenantId = tenantId,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
            JtiChainHead = jtiChainHead,
            CreatedAt = DateTime.UtcNow,
        };
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync();
        return token;
    }

    public async Task<RefreshToken?> GetByTokenHashAsync(string tokenHash)
        => await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

    public Task RevokeAsync(Guid id)
        => RevokeAsync(id, RefreshTokenRevokedReasons.ManualLogout);

    public async Task RevokeAsync(Guid id, string reason)
    {
        EnsureKnownReason(reason);
        var token = await db.RefreshTokens.FindAsync(id);
        if (token is not null && token.RevokedAt is null)
        {
            token.RevokedAt = DateTime.UtcNow;
            token.RevokedReason = reason;
            await db.SaveChangesAsync();
        }
    }

    public Task<int> RevokeAllForUserAsync(Guid userId)
        => RevokeAllForUserAsync(userId, RefreshTokenRevokedReasons.LogoutAll);

    public async Task<int> RevokeAllForUserAsync(Guid userId, string reason)
    {
        EnsureKnownReason(reason);
        var tokens = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var token in tokens)
        {
            token.RevokedAt = now;
            token.RevokedReason = reason;
        }
        await db.SaveChangesAsync();
        return tokens.Count;
    }

    public async Task<IReadOnlyList<RefreshToken>> FindByJtiChainHeadAsync(Guid chainHead)
    {
        if (chainHead == Guid.Empty) return Array.Empty<RefreshToken>();
        return await db.RefreshTokens
            .Where(t => t.JtiChainHead == chainHead && t.RevokedAt == null)
            .ToListAsync();
    }

    public async Task<int> RevokeChainAsync(Guid chainHead, string reason)
    {
        EnsureKnownReason(reason);
        if (chainHead == Guid.Empty) return 0;
        var tokens = await db.RefreshTokens
            .Where(t => t.JtiChainHead == chainHead && t.RevokedAt == null)
            .ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var token in tokens)
        {
            token.RevokedAt = now;
            token.RevokedReason = reason;
        }
        await db.SaveChangesAsync();
        return tokens.Count;
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

    /// <summary>
    /// Story 28-9 AC3 — defence-in-depth check. The DB has a CHECK
    /// constraint that rejects unknown reasons; this client-side guard
    /// turns a "SQL error from a typo" into a clearer ArgumentException
    /// at the offending call site, with the actual offending value in
    /// the message.
    /// </summary>
    private static void EnsureKnownReason(string reason)
    {
        if (reason is RefreshTokenRevokedReasons.ManualLogout
            or RefreshTokenRevokedReasons.LogoutAll
            or RefreshTokenRevokedReasons.RotationConsumed
            or RefreshTokenRevokedReasons.SwitchOrg
            or RefreshTokenRevokedReasons.ReuseDetected
            or RefreshTokenRevokedReasons.PasswordReset
            or RefreshTokenRevokedReasons.AdminForceLogout)
        {
            return;
        }
        throw new ArgumentException(
            $"Unknown refresh-token revoke reason '{reason}'. See {nameof(RefreshTokenRevokedReasons)}.",
            nameof(reason));
    }
}
