namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-6 — per-tenant entitlement snapshot cache. Keyed by the resolved
/// <c>tenant_id</c>. A cached snapshot is a memory optimisation only, never a
/// correctness mechanism: a pinned plan version is immutable, so a stale entry
/// can never be WRONG — only outdated if a tenant was re-assigned, which the
/// <c>TENANT.PLAN.CHANGED</c> event invalidates instantly. The TTL is a memory
/// bound.
/// </summary>
public interface IEntitlementSnapshotCache
{
    /// <summary>
    /// Return the cached snapshot for a tenant, or <c>null</c> on miss or
    /// expiry. Expired entries are evicted lazily on read.
    /// </summary>
    ResolvedEntitlements? TryGet(Guid tenantId);

    /// <summary>Cache (or overwrite) a tenant's snapshot with the default TTL.</summary>
    void Set(Guid tenantId, ResolvedEntitlements resolved);

    /// <summary>Evict exactly one tenant's snapshot (on <c>TENANT.PLAN.CHANGED</c>).</summary>
    void Invalidate(Guid tenantId);

    /// <summary>Clear the whole cache (on a catalog-wide edit).</summary>
    void Flush();

    /// <summary>Current entry count — exposed for tests + diagnostics.</summary>
    int Count { get; }
}
