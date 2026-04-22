namespace Tamma.Data.Entities;

/// <summary>
/// Story 28-7 deferred-item: CP-side routing index for prefix-encoded API
/// keys. Maps the on-wire key prefix (e.g. the first ~12 chars of
/// <c>tamma_sk_t_&lt;b32-tenant&gt;_&lt;rand&gt;...</c>) to the owning
/// tenant + the <c>api_keys.Id</c> row so the auth handler can do an O(1)
/// lookup instead of either:
/// <list type="bullet">
///   <item>parsing the prefix on every request (current behaviour — still
///         used as a fallback when the index row is missing), or</item>
///   <item>scanning every <c>api_keys</c> row by <c>KeyHash</c>
///         (pre-Story-28-7 behaviour).</item>
/// </list>
///
/// <para>Row semantics:</para>
/// <list type="bullet">
///   <item><see cref="KeyPrefix"/> — the 10–12 char display prefix. Primary
///         key so we never write two index rows per key. For tenant-scoped
///         (<c>tamma_sk_t_</c>) keys this is the banner; for platform/user
///         keys it is the full canonical display prefix from
///         <c>ApiKeyHasher.Prefix</c>.</item>
///   <item><see cref="HashedSuffix"/> — SHA-256 of the random suffix so the
///         auth handler can do a constant-time equality check without
///         persisting the plaintext key material anywhere. Belt-and-braces
///         defence: even if the routing index leaks, the suffix hash is
///         useless without the raw key (attacker still has to brute-force
///         the 256-bit random). Nullable so rows can be inserted before the
///         suffix hash is computed (lets the CP insert come first in the
///         two-phase create flow; see the endpoints impl plan).</item>
///   <item><see cref="TenantId"/> — nullable. Null for platform-admin rows.
///         Tenant and user rows carry the effective tenant id.</item>
///   <item><see cref="ApiKeyId"/> — the row in <c>api_keys</c>. Auth handler
///         uses it as the <c>GetByIdAsync</c> key once the index resolves.</item>
///   <item><see cref="Scope"/> — mirrors <c>api_keys.Scope</c> so we can
///         filter by scope without joining back to the full table.</item>
///   <item><see cref="RevokedAt"/> — mirror of <c>api_keys.RevokedAt</c>
///         (soft-revoke + 24h rotation grace). Kept in sync by
///         <see cref="Repositories.IPlatformApiKeyIndexRepository.RevokeAsync"/>.</item>
/// </list>
/// </summary>
public class PlatformApiKeyIndex
{
    /// <summary>
    /// On-wire display prefix (primary key). Matches
    /// <c>ApiKeyHasher.Prefix</c> for platform/user keys; for tenant-scoped
    /// keys the full <c>tamma_sk_t_&lt;b32&gt;_</c> would be too long, so we
    /// still store the 12-char display prefix. Column length matches
    /// <c>api_keys.KeyPrefix</c> (varchar(16)).
    /// </summary>
    public string KeyPrefix { get; set; } = null!;

    /// <summary>
    /// SHA-256 hex of the random suffix (post-scope-marker bytes of the raw
    /// key). Constant-time equality check on the route-lookup path so the
    /// handler doesn't need to compute Argon2 just to find the row — only
    /// to confirm the match once a candidate is found.
    /// </summary>
    public string HashedSuffix { get; set; } = null!;

    /// <summary>
    /// Owning tenant id for <see cref="Scope"/>=<c>tenant</c> or
    /// <c>user</c>. Null for <c>platform</c>/<c>service</c>/<c>installation</c>
    /// rows.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>FK to <c>api_keys.Id</c> (one-to-one).</summary>
    public Guid ApiKeyId { get; set; }

    /// <summary>
    /// Mirror of <c>api_keys.Scope</c> (e.g. <c>tenant</c>, <c>platform</c>,
    /// <c>user</c>, <c>service</c>, <c>installation</c>). Denormalised so the
    /// auth handler can enforce scope-specific routing rules without a
    /// secondary lookup.
    /// </summary>
    public string Scope { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Mirror of <c>api_keys.RevokedAt</c>. Future-dated values represent the
    /// 24h rotation grace window (key still valid until the timestamp passes).
    /// </summary>
    public DateTime? RevokedAt { get; set; }
}
