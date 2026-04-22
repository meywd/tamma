using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 28-7 deferred-item: CP-scoped repository for the
/// <c>platform_api_key_index</c> routing table. Enables O(1) prefix →
/// tenant+apiKeyId lookups during auth.
/// </summary>
public interface IPlatformApiKeyIndexRepository
{
    /// <summary>Inserts a new index row.</summary>
    Task<PlatformApiKeyIndex> CreateAsync(PlatformApiKeyIndex row);

    /// <summary>
    /// Looks up a row by its display prefix. Returns <c>null</c> if no
    /// matching row exists (falls back to prefix-parse routing).
    /// </summary>
    Task<PlatformApiKeyIndex?> GetByPrefixAsync(string keyPrefix);

    /// <summary>
    /// Looks up a row by its display prefix AND a candidate hashed suffix.
    /// Constant-time check via SQL equality; used by the auth handler so a
    /// probing caller can't distinguish between "prefix unknown" and "suffix
    /// mismatch" by timing.
    /// </summary>
    Task<PlatformApiKeyIndex?> GetByPrefixAndSuffixAsync(string keyPrefix, string hashedSuffix);

    /// <summary>
    /// Marks an index row as revoked by <c>ApiKeyId</c>. Mirrors the
    /// <c>api_keys</c> soft-revoke — sets <see cref="PlatformApiKeyIndex.RevokedAt"/>
    /// to <paramref name="revokedAt"/> (null = now; future-dated = 24h grace).
    /// </summary>
    Task RevokeByApiKeyIdAsync(Guid apiKeyId, DateTime? revokedAt = null);

    /// <summary>Hard-deletes an index row by prefix. Used by two-phase create rollback.</summary>
    Task DeleteByPrefixAsync(string keyPrefix);
}
