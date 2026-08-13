using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms;

/// <summary>
/// Story 31-2 driver cache. Wraps
/// <see cref="IMemoryCache"/> with a typed
/// <c>(tenantId, kind)</c>-keyed surface so the
/// <see cref="PlatformResolver"/> doesn't have to hand-roll cache-key
/// formatting at every call site.
///
/// <para>Behavior summary (Story 31-2 AC4):</para>
/// <list type="bullet">
///   <item><b>TTL</b>: 5-minute sliding expiration PLUS an absolute
///         bound (<see cref="PlatformDriverCacheOptions.AbsoluteTtl"/>,
///         default 5 minutes). The absolute bound is what makes "a
///         missed invalidation event still self-heals within the
///         window" literally true: sliding expiration alone renews on
///         every hit, so a tenant with sustained traffic (the CI
///         poller resolves every 30s) kept a stale driver — and its
///         compose-time credential — alive FOREVER (Epic 31 review,
///         F-medium).</item>
///   <item><b>Capacity</b>: configurable via
///         <see cref="PlatformDriverCacheOptions.MaxEntries"/>;
///         default 512.</item>
///   <item><b>Invalidation</b>: explicit per-tenant via
///         <see cref="InvalidateTenantAsync"/> (called by the
///         event-listener tail on
///         <c>PLATFORM.INSTALLATION.CREDENTIAL_ROTATED</c>,
///         <c>PLATFORM.INSTALLATION.DISCONNECTED</c>,
///         <c>TENANT.SWITCH_ORG</c>).</item>
/// </list>
///
/// <para>The cache is registered as a singleton; the resolver is
/// scoped, so each request goes through one shared cache instance —
/// hits are constant-time across the process.</para>
/// </summary>
public sealed class PlatformDriverCache : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _slidingTtl;
    private readonly TimeSpan _absoluteTtl;
    private readonly int _maxEntries;
    private readonly ILogger<PlatformDriverCache> _logger;
    // Tracks which (tenantId, kind) keys live in the cache so
    // InvalidateTenantAsync can purge all entries for a tenant in
    // O(1) per-key without iterating the cache.
    private readonly object _indexLock = new();
    private readonly Dictionary<Guid, HashSet<PlatformKind>> _tenantIndex = new();

    public PlatformDriverCache(
        PlatformDriverCacheOptions? options = null,
        ILogger<PlatformDriverCache>? logger = null)
    {
        options ??= new PlatformDriverCacheOptions();
        _slidingTtl = options.SlidingTtl;
        _absoluteTtl = options.AbsoluteTtl;
        _maxEntries = options.MaxEntries;
        _logger = logger ?? NullLogger<PlatformDriverCache>.Instance;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = _maxEntries,
            // Compaction triggers when the cache approaches the
            // size limit; a small percentage keeps eviction smooth
            // without thrashing under steady-state load.
            CompactionPercentage = 0.10,
        });
    }

    /// <summary>
    /// Try to fetch a cached driver. Returns true + the driver on
    /// hit; false on miss (caller composes a fresh driver).
    /// </summary>
    public bool TryGet(
        Guid tenantId,
        PlatformKind kind,
        out IGitPlatformDriver? driver)
    {
        if (_cache.TryGetValue(MakeKey(tenantId, kind), out var value)
            && value is IGitPlatformDriver cached)
        {
            driver = cached;
            return true;
        }
        driver = null;
        return false;
    }

    /// <summary>
    /// Insert a freshly-composed driver under
    /// <c>(tenantId, kind)</c>. Replaces any existing entry under the
    /// same key (so a re-resolve after rotation lands cleanly without
    /// an explicit invalidation).
    /// </summary>
    public void Set(Guid tenantId, PlatformKind kind, IGitPlatformDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        var key = MakeKey(tenantId, kind);

        var entryOptions = new MemoryCacheEntryOptions
        {
            Size = 1,
            SlidingExpiration = _slidingTtl,
            // The HARD staleness bound — sliding expiration renews on
            // every hit, so without this a hot tenant's entry (and its
            // compose-time credential) never expired (Epic 31 review).
            AbsoluteExpirationRelativeToNow = _absoluteTtl,
            // When the cache evicts the entry, drop it from the
            // tenant index so InvalidateTenantAsync doesn't try to
            // re-evict a stale key.
            PostEvictionCallbacks =
            {
                new PostEvictionCallbackRegistration
                {
                    EvictionCallback = (k, _, _, _) => OnEvicted(k),
                },
            },
        };

        _cache.Set(key, driver, entryOptions);

        lock (_indexLock)
        {
            if (!_tenantIndex.TryGetValue(tenantId, out var kinds))
            {
                kinds = new HashSet<PlatformKind>();
                _tenantIndex[tenantId] = kinds;
            }
            kinds.Add(kind);
        }
    }

    /// <summary>
    /// Drop every cached driver for a tenant. Used by the event-tail
    /// listener on rotation / disconnect / switch-org events.
    /// </summary>
    public Task InvalidateTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        HashSet<PlatformKind>? kinds;
        lock (_indexLock)
        {
            if (!_tenantIndex.TryGetValue(tenantId, out kinds))
            {
                return Task.CompletedTask;
            }
            // Snapshot — eviction callbacks will mutate the index.
            kinds = new HashSet<PlatformKind>(kinds);
        }

        foreach (var kind in kinds)
        {
            _cache.Remove(MakeKey(tenantId, kind));
        }

        _logger.LogDebug(
            "Invalidated platform driver cache for tenant {TenantId} ({Count} entries)",
            tenantId, kinds.Count);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drop a single cached driver. Used by tests and by targeted
    /// rotations (a per-installation rotation event only invalidates
    /// the matching kind, not every kind for the tenant).
    /// </summary>
    public void Invalidate(Guid tenantId, PlatformKind kind)
    {
        _cache.Remove(MakeKey(tenantId, kind));
    }

    /// <summary>
    /// Test seam — total entry count across all tenants. Production
    /// code shouldn't reach into the cache size.
    /// </summary>
    internal int Count => _cache.Count;

    public void Dispose() => _cache.Dispose();

    private static string MakeKey(Guid tenantId, PlatformKind kind) =>
        $"{tenantId:N}:{(int)kind}";

    private void OnEvicted(object key)
    {
        // Parse the key back into (tenantId, kind) and prune the
        // index. Cheap: this only runs on actual evictions.
        if (key is not string s) return;
        var sep = s.IndexOf(':');
        if (sep <= 0 || sep + 1 >= s.Length) return;

        if (!Guid.TryParseExact(s.AsSpan(0, sep), "N", out var tenantId)) return;
        if (!int.TryParse(s.AsSpan(sep + 1), out var kindInt)) return;

        var kind = (PlatformKind)kindInt;
        lock (_indexLock)
        {
            if (_tenantIndex.TryGetValue(tenantId, out var kinds))
            {
                kinds.Remove(kind);
                if (kinds.Count == 0)
                {
                    _tenantIndex.Remove(tenantId);
                }
            }
        }
    }
}

/// <summary>
/// Configuration knobs for <see cref="PlatformDriverCache"/>. Wired
/// from the <c>Platforms:DriverCache</c> config section in
/// <c>Program.cs</c>; see Story 31-2 §9 for the rationale on
/// defaults.
/// </summary>
public sealed class PlatformDriverCacheOptions
{
    /// <summary>
    /// Sliding expiration window. Default 5 minutes — evicts idle
    /// tenants' entries early. NOT the staleness guarantee: hits renew
    /// it, so the bound for an active tenant is
    /// <see cref="AbsoluteTtl"/>.
    /// </summary>
    public TimeSpan SlidingTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Absolute expiration (relative to insertion) — the HARD bound on
    /// how long a composed driver (and the credential baked into it at
    /// compose time) can be served regardless of hit rate. Default 5
    /// minutes, matching the documented "self-heals in ≤5 minutes"
    /// safety property (Story 31-2 §9; Epic 31 review, F-medium: the
    /// sliding-only cache violated it for any tenant with sub-window
    /// traffic).
    /// </summary>
    public TimeSpan AbsoluteTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Capacity (entry count). Default 512 — per Story 31-2 §9, this
    /// covers a 1000-tenant deployment with hot-tenant skew before
    /// the eviction policy fires.
    /// </summary>
    public int MaxEntries { get; init; } = 512;
}
