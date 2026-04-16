using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IPasswordResetRepository
{
    Task<PasswordResetToken> CreateAsync(Guid userId, string tokenHash, DateTime expiresAt);
    Task<PasswordResetToken?> GetByTokenHashAsync(string tokenHash);
    Task ConsumeAsync(Guid id);
    Task<int> CleanExpiredAsync();
}
