using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IRefreshTokenRepository
{
    Task<RefreshToken> CreateAsync(Guid userId, string tokenHash, DateTime expiresAt);
    Task<RefreshToken?> GetByTokenHashAsync(string tokenHash);
    Task RevokeAsync(Guid id);
    Task RevokeAllForUserAsync(Guid userId);
    Task<int> CleanExpiredAsync();
}
