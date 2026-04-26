namespace Tamma.Api.Services.TenantStatus;

/// <summary>
/// Story 28-8 AC3 — short-TTL cache of <c>tenants.Status</c> values
/// keyed by tenant id. The middleware consults this cache before
/// hitting CP for every authenticated request — a tenant whose status
/// hasn't changed in the last few seconds reuses the cached value
/// instead of paying a DB round-trip per request.
///
/// <para><b>Coherence model</b>: per-pod in-memory only in the current
/// implementation. Status flips that originate on this pod (admin
/// endpoints, lifecycle workflow steps) call
/// <see cref="Invalidate"/> immediately so the next read on this pod
/// re-fetches from CP. Status flips that originate on a sibling pod
/// converge after the TTL elapses (default 10s) — a brief window of
/// stale data is acceptable per the Doc 03 §6.2 design.</para>
///
/// <para><b>Cluster-wide invalidation</b>: a future enhancement can
/// wire a RabbitMQ / Postgres LISTEN-NOTIFY broadcaster that calls
/// <see cref="Invalidate"/> on every pod when a status flip lands.
/// The cache contract is shaped to support that drop-in (the impl
/// just needs to subscribe + dispatch).</para>
/// </summary>
public interface ITenantStatusCache
{
    /// <summary>
    /// Look up the cached status. Returns <c>true</c> + the cached
    /// value when fresh; <c>false</c> when missing or expired (caller
    /// must read CP + call <see cref="Set"/>).
    /// </summary>
    bool TryGet(Guid tenantId, out string? status);

    /// <summary>
    /// Cache <paramref name="status"/> for <paramref name="tenantId"/>.
    /// The entry expires after the cache's configured TTL
    /// (<see cref="TenantStatusCacheOptions.TtlSeconds"/>).
    /// </summary>
    void Set(Guid tenantId, string? status);

    /// <summary>
    /// Forget the cached entry for <paramref name="tenantId"/>. Called
    /// by every code path that mutates <c>tenants.Status</c> (admin
    /// endpoints, lifecycle activities, the cleanup workflow). Idempotent
    /// — invalidating a missing entry is a no-op.
    /// </summary>
    void Invalidate(Guid tenantId);
}

/// <summary>
/// Options for the in-memory tenant status cache. Bound to the
/// <c>TenantStatusCache</c> configuration section.
/// </summary>
public sealed class TenantStatusCacheOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "TenantStatusCache";

    /// <summary>Cache entry TTL in seconds. Default 10 per Doc 03
    /// §6.2.</summary>
    public int TtlSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum number of cached entries before LRU eviction kicks in.
    /// Caps memory under tenant churn — the cache is a perf hint, not
    /// a source of truth. Default 10000 (covers the largest expected
    /// active-tenant population without unbounded growth).
    /// </summary>
    public int MaxEntries { get; set; } = 10000;
}
