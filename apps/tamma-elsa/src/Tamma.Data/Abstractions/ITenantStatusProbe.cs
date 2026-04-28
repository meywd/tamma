namespace Tamma.Data.Abstractions;

/// <summary>
/// Story 28-8 H12 — strictly <b>read-only</b> probe over the per-pod
/// tenant-status cache. Defined in <c>Tamma.Data.Abstractions</c> so
/// the <see cref="ITenantConnectionResolver"/> implementation
/// (in <c>Tamma.Data.Pooling</c>) can consult the cache from its hot
/// path without taking a project reference on the API layer where the
/// concrete cache lives.
///
/// <para><b>Read-only contract:</b> the surface is intentionally
/// limited to <see cref="TryGet"/>. Cache invalidation belongs to the
/// auth surface — admin endpoints + the cluster-wide
/// <c>TenantStatusInvalidationListener</c> — and is performed via
/// <c>ITenantStatusCache.Invalidate</c> directly. The resolver never
/// invalidates the cache: a cache-flip detection on the hot path drives
/// an evict-then-rebuild on the resolver's own pool, leaving cache
/// invalidation to the auth-side writers. Surfacing
/// <c>Invalidate</c> here would invite cross-cutting writes from the
/// pooling layer that violate the read-mostly architecture
/// (PF-C6 cleanup).</para>
///
/// <para><b>Why a probe and not the full cache contract</b>: surfacing
/// the full cache (with <c>Set</c> / <c>Invalidate</c>) here would
/// invite the resolver to own status writes, which belongs to the auth
/// surface (middleware / admin endpoints / NOTIFY listener) where the
/// writeable cache instance lives.</para>
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
}
