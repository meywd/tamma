namespace Tamma.Data.Pooling;

/// <summary>
/// Story 28-4 AC5 — admin-only diagnostics surface for the per-tenant
/// connection pool. Implemented by <see cref="LruPooledTenantConnectionResolver"/>
/// (the production resolver). Test doubles + the legacy
/// <c>StubTenantConnectionResolver</c> do NOT implement this interface so
/// the admin endpoint can return a 404 / 503 cleanly when running in a
/// non-production wiring.
///
/// <para>The detailed snapshot here is a strict superset of
/// <see cref="Tamma.Data.Abstractions.TenantConnectionPoolStats"/> —
/// callers that just want the warm-pool count can keep using the
/// resolver's <c>GetStats()</c>; admins call
/// <see cref="GetDetailedStats"/> for hit/miss/hit-ratio/eviction-by-reason.
/// Per-tenant entries are surfaced separately via
/// <see cref="ListWarmTenants"/> so an operator can spot a hot tenant
/// holding the cache hostage.</para>
/// </summary>
public interface IAdminPoolDiagnostics
{
    /// <summary>
    /// One-shot snapshot of process-wide pool counters: cache hits,
    /// misses, opens, evictions broken down by reason (lru / explicit /
    /// rotation), and the lifetime hit ratio. Cheap — counters are
    /// <see cref="System.Threading.Interlocked"/>-read atomics.
    /// </summary>
    DetailedPoolStats GetDetailedStats();

    /// <summary>
    /// List the currently-warm tenants in MRU order (most-recently-used
    /// first), capped at <paramref name="limit"/>. Each entry carries
    /// the tenant id and the outstanding lease count (handle ref count;
    /// 0 if no <c>LeaseAsync</c>-style consumers exist). Useful for
    /// spotting a long-running SSE stream that's preventing eviction.
    /// </summary>
    /// <param name="limit">Max entries to return. Clamped 1..1000.</param>
    IReadOnlyList<WarmTenantEntry> ListWarmTenants(int limit);
}

/// <summary>
/// Detailed pool counters for the admin diagnostics endpoint. Strict
/// superset of <see cref="Tamma.Data.Abstractions.TenantConnectionPoolStats"/>
/// — kept as a separate record so the ITenantConnectionResolver
/// interface stays narrow.
/// </summary>
/// <param name="WarmPoolCount">Warm pools currently in the LRU cache.</param>
/// <param name="OpenedTotal">Total Npgsql data sources built since startup
/// (one per cache miss that successfully built).</param>
/// <param name="EvictedTotal">Total evictions since startup, all
/// reasons.</param>
/// <param name="EvictedByLru">Subset of <paramref name="EvictedTotal"/>
/// caused by the LRU cap.</param>
/// <param name="EvictedExplicit">Subset of <paramref name="EvictedTotal"/>
/// caused by an explicit <c>EvictAsync</c> (delete / rotation).</param>
/// <param name="HitsTotal">Cache hits.</param>
/// <param name="MissesTotal">Cache misses.</param>
/// <param name="HitRatio">Lifetime <c>hits / (hits + misses)</c>; 0 when
/// no requests have been served.</param>
/// <param name="DeferredDisposeBacklog">Round-2 H5 — number of
/// background <c>NpgsqlDataSource.DisposeAsync</c> tasks that have been
/// scheduled but not yet completed. A persistently high value indicates
/// either a slow Postgres or a leaking consumer that's preventing the
/// dispose path from making progress. Drained at process shutdown
/// before the resolver's own dispose returns.</param>
/// <param name="MaxOutstandingLeases">Round-2 M7 — configured
/// per-tenant lease ceiling. Surfaces the value of
/// <see cref="TenantConnectionPoolOptions.MaxOutstandingLeases"/> so
/// admins can tune from the diagnostics endpoint without re-reading
/// appsettings.</param>
/// <param name="TotalOutstandingLeases">Round-2 M7 — sum of outstanding
/// leases across every warm tenant. A leading indicator of cache
/// pressure / leaking SSE consumers.</param>
/// <param name="BuildLocksRetained">Round-2 M13 — number of per-tenant
/// build-time semaphores currently held in the resolver's
/// <c>_buildLocks</c> dictionary. Should converge to roughly the warm
/// pool count over time; persistent growth signals the M13 trim path
/// is broken.</param>
public sealed record DetailedPoolStats(
    int WarmPoolCount,
    long OpenedTotal,
    long EvictedTotal,
    long EvictedByLru,
    long EvictedExplicit,
    long HitsTotal,
    long MissesTotal,
    double HitRatio,
    int DeferredDisposeBacklog,
    int MaxOutstandingLeases,
    int TotalOutstandingLeases,
    int BuildLocksRetained);

/// <summary>
/// Per-tenant entry in <see cref="IAdminPoolDiagnostics.ListWarmTenants"/>.
/// </summary>
/// <param name="TenantId">Tenant id.</param>
/// <param name="OutstandingLeases">Open <see cref="TenantConnectionHandle"/>
/// references (0 if no leases). A non-zero value means an
/// <c>EvictAsync</c> would defer until those leases release.</param>
public sealed record WarmTenantEntry(
    Guid TenantId,
    int OutstandingLeases);
