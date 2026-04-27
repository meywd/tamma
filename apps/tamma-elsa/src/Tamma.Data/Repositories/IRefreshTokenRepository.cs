using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IRefreshTokenRepository
{
    Task<RefreshToken> CreateAsync(Guid userId, string tokenHash, DateTime expiresAt);
    Task<RefreshToken?> GetByTokenHashAsync(string tokenHash);
    Task RevokeAsync(Guid id);

    /// <summary>
    /// Revokes every active refresh token for the user and returns the number
    /// of rows that flipped from active → revoked. Story 28-R2 / H2 — the
    /// count is surfaced in the <c>USER.LOGOUT_ALL.SUCCESS</c> /
    /// <c>USER.ORG_SWITCHED.SUCCESS</c> audit events so SIEM can flag mass
    /// revocations (e.g. attacker burning every device after credential
    /// theft). A return value of 0 means the call was a no-op (already
    /// revoked / never had any).
    /// </summary>
    Task<int> RevokeAllForUserAsync(Guid userId);
    Task<int> CleanExpiredAsync();
}
