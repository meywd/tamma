using System.Diagnostics.Metrics;

namespace Tamma.Data.Pooling;

/// <summary>
/// OpenTelemetry-compatible metric surface for
/// <see cref="LruPooledTenantConnectionResolver"/>. Names match the
/// Story 28-4 user task scope:
/// <list type="bullet">
///   <item><description><c>tamma.tenant_pools.warm</c> — observable
///     gauge of the current LRU cache size.</description></item>
///   <item><description><c>tamma.tenant_pools.opened_total</c> —
///     monotonic counter of pools created since process start
///     (<c>opened</c> = "Npgsql data source built").</description></item>
///   <item><description><c>tamma.tenant_pools.evicted_total</c> —
///     monotonic counter of evictions, with a <c>reason</c> tag
///     (<c>lru</c>, <c>explicit</c>, <c>rotation</c>).</description></item>
///   <item><description><c>tamma.tenant_pools.cache_hit_ratio</c> —
///     observable gauge in <c>[0, 1]</c> showing the rolling lifetime
///     ratio <c>hits / (hits + misses)</c>; emits <c>0</c> until at
///     least one access has been served.</description></item>
/// </list>
///
/// <para>The class also exposes plain <see cref="long"/> counters
/// (<see cref="WarmPoolCount"/>, <see cref="OpenedTotal"/>,
/// <see cref="EvictedTotal"/>, <see cref="HitsTotal"/>,
/// <see cref="MissesTotal"/>) so the legacy
/// <see cref="Tamma.Data.Abstractions.TenantConnectionPoolStats"/>
/// snapshot exposed by <c>ITenantConnectionResolver.GetStats</c> can
/// be populated without hitting the Meter API on the hot path.</para>
/// </summary>
public sealed class TenantConnectionPoolMetrics : IDisposable
{
    /// <summary>Public meter name — pin so dashboards stay stable.</summary>
    public const string MeterName = "Tamma.TenantConnectionPool";

    private readonly Meter _meter;
    private readonly Counter<long> _opened;
    private readonly Counter<long> _evicted;

    private long _warm;
    private long _hits;
    private long _misses;
    private long _openedTotal;
    private long _evictedTotal;

    public TenantConnectionPoolMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        _opened = _meter.CreateCounter<long>(
            "tamma.tenant_pools.opened_total",
            unit: "{pool}",
            description: "Total per-tenant Npgsql data sources built since process start.");

        _evicted = _meter.CreateCounter<long>(
            "tamma.tenant_pools.evicted_total",
            unit: "{pool}",
            description: "Total per-tenant pools evicted from the LRU cache.");

        _meter.CreateObservableGauge(
            "tamma.tenant_pools.warm",
            () => Interlocked.Read(ref _warm),
            unit: "{pool}",
            description: "Current count of warm per-tenant pools held in the LRU cache.");

        _meter.CreateObservableGauge<double>(
            "tamma.tenant_pools.cache_hit_ratio",
            () =>
            {
                var hits = Interlocked.Read(ref _hits);
                var misses = Interlocked.Read(ref _misses);
                var total = hits + misses;
                return total == 0 ? 0d : (double)hits / total;
            },
            unit: "1",
            description: "Lifetime cache hit ratio for tenant connection lookups.");
    }

    /// <summary>Live count of warm pools. Read by <c>GetStats</c>.</summary>
    public long WarmPoolCount => Interlocked.Read(ref _warm);

    /// <summary>Lifetime opens (cache misses that built a new pool).</summary>
    public long OpenedTotal => Interlocked.Read(ref _openedTotal);

    /// <summary>Lifetime evictions (any reason).</summary>
    public long EvictedTotal => Interlocked.Read(ref _evictedTotal);

    /// <summary>Lifetime cache hits.</summary>
    public long HitsTotal => Interlocked.Read(ref _hits);

    /// <summary>Lifetime cache misses.</summary>
    public long MissesTotal => Interlocked.Read(ref _misses);

    /// <summary>Bumped on a cache hit.</summary>
    public void RecordHit() => Interlocked.Increment(ref _hits);

    /// <summary>Bumped on a cache miss BEFORE the build attempt.</summary>
    public void RecordMiss() => Interlocked.Increment(ref _misses);

    /// <summary>
    /// Bumped after a successful <c>NpgsqlDataSource</c> build. Also
    /// increments the warm-pool gauge by one.
    /// </summary>
    public void RecordOpened()
    {
        Interlocked.Increment(ref _openedTotal);
        Interlocked.Increment(ref _warm);
        _opened.Add(1);
    }

    /// <summary>
    /// Bumped on any eviction path. Also decrements the warm-pool
    /// gauge by one. The <paramref name="reason"/> tag is propagated to
    /// the OTel counter so dashboards can break down by cause.
    /// </summary>
    public void RecordEviction(string reason)
    {
        Interlocked.Increment(ref _evictedTotal);
        Interlocked.Decrement(ref _warm);
        _evicted.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    public void Dispose() => _meter.Dispose();
}
