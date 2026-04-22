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

    /// <summary>
    /// Story 28-7 deferred-item — rewrites <c>api_keys.KeyHash</c> in place
    /// to upgrade a legacy SHA-256 / scrypt row to Argon2id on next
    /// successful verify. No row rotation, no new key material.
    /// </summary>
    Task UpdateHashAsync(Guid id, string newKeyHash);
}
