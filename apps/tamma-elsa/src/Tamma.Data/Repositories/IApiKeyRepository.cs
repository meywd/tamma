using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IApiKeyRepository
{
    Task<ApiKey> CreateAsync(ApiKey apiKey);
    Task<ApiKey?> GetByIdAsync(Guid id);
    Task<ApiKey?> GetByHashAsync(string keyHash);
    Task<List<ApiKey>> ListByScopeAsync(string scope);
    Task<List<ApiKey>> ListByOwnerAsync(string ownerId);
    Task RevokeAsync(Guid id);

    /// <summary>
    /// Bulk-revoke every key owned by the given owner-id (user GUID for
    /// scope='user'; service-name string for scope='service'). Used by the
    /// admin <c>DeleteUser</c> cascade — see audit finding 019.
    /// </summary>
    Task RevokeAllByOwnerAsync(string ownerId);

    Task<ApiKey> RotateAsync(Guid oldId, string newKeyHash, string newKeyPrefix);
    Task UpdateLastUsedAsync(Guid id);
}
