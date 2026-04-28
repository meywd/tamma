namespace Tamma.Data.Pooling;

/// <summary>
/// Options bound to the <c>TenantConnectionPool</c> configuration
/// section. Drives the per-tenant Npgsql pool sizing and the LRU cache
/// shape inside <see cref="LruPooledTenantConnectionResolver"/>.
///
/// <para>Defaults match the user task scope for Story 28-4:
/// <c>MaxEntries=500</c>, <c>MaxPoolSize=5</c>. The resolver re-reads
/// these via <c>IOptionsMonitor</c> at construction so operators can
/// tune via <c>appsettings.json</c> without code changes; live reload
/// is intentionally NOT implemented in this story (Npgsql data sources
/// are immutable once created — see Story 28-12 for hot-rotation).</para>
/// </summary>
public sealed class TenantConnectionPoolOptions
{
    /// <summary>Configuration section name for binding.</summary>
    public const string SectionName = "TenantConnectionPool";

    /// <summary>
    /// Maximum number of warm <c>NpgsqlDataSource</c> instances kept in
    /// the LRU cache before eviction kicks in. Default 500. Hard cap on
    /// process-wide tenant pool memory.
    /// </summary>
    public int MaxEntries { get; set; } = 500;

    /// <summary>
    /// <c>NpgsqlConnectionStringBuilder.MaxPoolSize</c> for every
    /// per-tenant pool. Default 5 — limits worst-case Postgres backend
    /// pressure to <c>MaxEntries × MaxPoolSize</c> connections from this
    /// process.
    /// </summary>
    public int MaxPoolSize { get; set; } = 5;

    /// <summary>
    /// <c>NpgsqlConnectionStringBuilder.MinPoolSize</c>. Default 0 —
    /// idle tenants drain to zero connections so an idle tenant pays
    /// memory only, not Postgres backend slots.
    /// </summary>
    public int MinPoolSize { get; set; } = 0;

    /// <summary>
    /// Idle connection lifetime (seconds) before the Npgsql pool reaps
    /// it. Default 300 (5 minutes). Per Doc 04 §2.4.
    /// </summary>
    public int ConnectionIdleLifetimeSeconds { get; set; } = 300;

    /// <summary>
    /// Postgres connect timeout (seconds). Default 5.
    /// </summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Postgres command timeout (seconds). Default 30.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Npgsql keep-alive frequency (seconds). Default 30.
    /// </summary>
    public int KeepAliveSeconds { get; set; } = 30;

    /// <summary>
    /// Window (seconds) the resolver caches the CP <c>tenants</c> row
    /// lookup so an eviction storm doesn't hammer the control plane.
    /// Default 30.
    /// </summary>
    public int TenantRowCacheSeconds { get; set; } = 30;

    /// <summary>
    /// Round-2 M7 — number of retry attempts <c>LeaseAsync</c> makes when
    /// a race against eviction is observed (the cache entry vanishes
    /// between the cold-build and the handle acquisition). Each retry
    /// rebuilds via the cold path. Default 5; bumped from the previous
    /// hard-coded 3 because real eviction storms can knock out 4-5
    /// consecutive builds on a small cache.
    ///
    /// <para>Inter-attempt delay is <c>5ms × attempt</c> (so attempt 1
    /// waits 5ms, attempt 2 waits 10ms, …). Empirically this gives the
    /// LRU resolver enough breathing room without inflating tail
    /// latency on the success path.</para>
    /// </summary>
    public int LeaseRetryAttempts { get; set; } = 5;

    /// <summary>
    /// Round-2 M7 — per-tenant ceiling on outstanding
    /// <see cref="TenantConnectionHandle"/> instances. <c>LeaseAsync</c>
    /// throws <see cref="TenantLeaseLimitExceededException"/> when a new
    /// lease would push the live count above this value. Default 200 —
    /// generous enough for a busy SSE / long-running-consumer workload
    /// but low enough to alert on a leaking consumer before it starves
    /// other tenants.
    /// </summary>
    public int MaxOutstandingLeases { get; set; } = 200;
}
