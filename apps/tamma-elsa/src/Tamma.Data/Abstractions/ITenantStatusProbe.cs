namespace Tamma.Data.Abstractions;

/// <summary>
/// Story 28-8 H12 — read-only probe over the per-pod tenant-status
/// cache. Defined in <c>Tamma.Data.Abstractions</c> so the
/// <see cref="ITenantConnectionResolver"/> implementation
/// (in <c>Tamma.Data.Pooling</c>) can consult the cache from its hot
/// path without taking a project reference on the API layer where the
/// concrete cache lives.
///
/// <para><b>Why a probe and not the full cache contract</b>: the
/// resolver only needs <c>TryGet</c> + <c>Invalidate</c>. Surfacing
/// the full cache (with <c>Set</c>) here would invite the resolver to
/// own status writes, which belongs to the auth surface (middleware /
/// admin endpoints) where the writeable cache instance lives. The
/// resolver is read-mostly + eviction-only on this path.</para>
/// </summary>
public interface ITenantStatusProbe
{
    /// <summary>
    /// Look up the cached status. Returns <c>true</c> + the cached
    /// value when fresh; <c>false</c> when missing or expired. Treats
    /// <c>null</c> as a valid cached value (legacy rows have no
    /// status column populated yet).
    /// </summary>
    bool TryGet(Guid tenantId, out string? status);

    /// <summary>
    /// Forget the cached entry. Idempotent — invalidating a missing
    /// entry is a no-op. The resolver calls this when a cache-flip
    /// detection on the hot path forces a cold-CP refresh.
    /// </summary>
    void Invalidate(Guid tenantId);
}
