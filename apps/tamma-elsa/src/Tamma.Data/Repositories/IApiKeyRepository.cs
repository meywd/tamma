using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IApiKeyRepository
{
    Task<ApiKey> CreateAsync(ApiKey apiKey);
    Task<ApiKey?> GetByHashAsync(string keyHash);
    Task<List<ApiKey>> ListByScopeAsync(string scope);
    Task<List<ApiKey>> ListByOwnerAsync(string ownerId);
    Task RevokeAsync(Guid id);
    Task<ApiKey> RotateAsync(Guid oldId, string newKeyHash, string newKeyPrefix);
    Task UpdateLastUsedAsync(Guid id);
}
