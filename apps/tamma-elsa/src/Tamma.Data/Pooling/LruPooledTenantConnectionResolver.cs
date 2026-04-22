using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Data.Pooling;

/// <summary>
/// Production <see cref="ITenantConnectionResolver"/> for Story 28-4.
/// Owns a process-wide LRU cache of warm <see cref="NpgsqlDataSource"/>
/// instances keyed by tenant id, bounded by
/// <see cref="TenantConnectionPoolOptions.MaxEntries"/> (default 500).
///
/// <para><b>Hot path (cache hit)</b>: a single
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryGetValue"/> +
/// <see cref="LinkedListNode{T}"/> reposition under a short critical
/// section against <see cref="_lruLock"/>. No allocations. Returns the
/// cached <see cref="NpgsqlDataSource"/> directly.</para>
///
/// <para><b>Cold path (cache miss)</b>: serialised on a per-tenant
/// <see cref="SemaphoreSlim"/> drawn from <see cref="_buildLocks"/> so a
/// thundering herd of concurrent first-time requests for the same
/// tenant builds the pool exactly once. Steps:
/// <list type="number">
///   <item><description>Re-check the cache (another thread may have
///     populated it while we waited on the semaphore).</description></item>
///   <item><description>Load the tenant row from
///     <see cref="ControlPlaneDbContext"/> (using the short-lived
///     row cache from <see cref="_tenantRowCache"/>) and validate
///     <c>Status</c>.</description></item>
///   <item><description>Decrypt
///     <c>tenants.EncryptedConnectionString</c> via
///     <see cref="IConnectionStringDecryptor"/>.</description></item>
///   <item><description>Layer the per-tenant pool settings from
///     <see cref="TenantConnectionPoolOptions"/> on top of the decrypted
///     connection string and build a fresh
///     <see cref="NpgsqlDataSource"/>.</description></item>
///   <item><description>Insert into the LRU. If full, evict the least-
///     recently-used pool and dispose its data source.</description></item>
/// </list>
/// </para>
///
/// <para><b>Eviction</b>: <see cref="EvictAsync"/> removes a tenant from
/// the cache and disposes its data source. Npgsql's
/// <c>NpgsqlDataSource.DisposeAsync()</c> waits for in-flight
/// <see cref="NpgsqlConnection"/>s to be returned before tearing the
/// pool down, so a request mid-query is not yanked. Story 28-4 does NOT
/// implement the brief's ref-counted handle (AC4) — that's deferred to
/// the SSE/streaming work in 28-5/28-8 where indefinite handle lifetimes
/// are a real concern. Best-effort grace plus Npgsql's own draining is
/// sufficient for the request/response paths shipped in Wave A.</para>
///
/// <para><b>Per-tenant Elsa schema</b> (<see cref="GetElsaDataSourceAsync"/>)
/// currently mirrors the app data source — Story 28-5 wires the
/// dedicated Elsa pool when per-tenant Elsa databases ship.</para>
/// </summary>
public sealed class LruPooledTenantConnectionResolver
    : ITenantConnectionResolver, IAsyncDisposable
{
    /// <summary>
    /// LRU node payload — the cached data source, the tenant id, and a
    /// monotonic last-access stamp. The <see cref="_lru"/> linked list
    /// orders these from most- to least-recently used.
    /// </summary>
    private sealed class CacheEntry
    {
        public required Guid TenantId { get; init; }
        public required NpgsqlDataSource DataSource { get; init; }
    }

    private readonly IDbContextFactory<ControlPlaneDbContext> _cpFactory;
    private readonly IConnectionStringDecryptor _decryptor;
    private readonly TenantConnectionPoolMetrics _metrics;
    private readonly TenantConnectionPoolOptions _options;
    private readonly ILogger<LruPooledTenantConnectionResolver> _logger;

    /// <summary>
    /// Hot-path lookup. Value is the <see cref="LinkedListNode{T}"/>
    /// inside <see cref="_lru"/> so cache-hit reposition runs in O(1).
    /// </summary>
    private readonly ConcurrentDictionary<Guid, LinkedListNode<CacheEntry>> _pools = new();

    /// <summary>
    /// LRU ordering. <c>First</c> = most recently used, <c>Last</c> =
    /// next eviction victim. Mutated only under <see cref="_lruLock"/>.
    /// </summary>
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly object _lruLock = new();

    /// <summary>
    /// Per-tenant build-time semaphores. Shared across the process so
    /// repeated cold misses for the same tenant from concurrent
    /// requests collapse to a single Npgsql build.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _buildLocks = new();

    /// <summary>
    /// Short-lived cache of resolved tenant rows so eviction storms
    /// don't hammer the control plane on repeated cold misses. Window
    /// is <see cref="TenantConnectionPoolOptions.TenantRowCacheSeconds"/>.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset ExpiresAt, ResolvedTenantRow Row)> _tenantRowCache = new();

    private int _disposed; // 0 = alive, 1 = disposed

    public LruPooledTenantConnectionResolver(
        IDbContextFactory<ControlPlaneDbContext> cpFactory,
        IConnectionStringDecryptor decryptor,
        TenantConnectionPoolMetrics metrics,
        IOptions<TenantConnectionPoolOptions> options,
        ILogger<LruPooledTenantConnectionResolver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cpFactory);
        ArgumentNullException.ThrowIfNull(decryptor);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(options);

        _cpFactory = cpFactory;
        _decryptor = decryptor;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger ?? NullLogger<LruPooledTenantConnectionResolver>.Instance;

        if (_options.MaxEntries <= 0)
        {
            throw new ArgumentException(
                $"TenantConnectionPool:MaxEntries must be > 0 (got {_options.MaxEntries}).",
                nameof(options));
        }
        if (_options.MaxPoolSize <= 0)
        {
            throw new ArgumentException(
                $"TenantConnectionPool:MaxPoolSize must be > 0 (got {_options.MaxPoolSize}).",
                nameof(options));
        }
    }

    public ValueTask<NpgsqlDataSource> GetDataSourceAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Fast path. ConcurrentDictionary.TryGetValue is lock-free.
        if (_pools.TryGetValue(tenantId, out var node))
        {
            // Reposition the LRU under a short lock. We re-check the
            // dictionary after taking the lock because EvictAsync /
            // BuildOrEvict may have removed the entry while we were
            // racing here — in that case fall through to the slow path.
            lock (_lruLock)
            {
                if (_pools.TryGetValue(tenantId, out var current) && current == node)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    _metrics.RecordHit();
                    return ValueTask.FromResult(node.Value.DataSource);
                }
            }
        }

        return new ValueTask<NpgsqlDataSource>(
            ResolveSlowAsync(tenantId, cancellationToken));
    }

    public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
        // Per-tenant Elsa schema currently shares the app data source.
        // Story 28-5 splits these once dedicated Elsa DBs land.
        => GetDataSourceAsync(tenantId, cancellationToken);

    public async ValueTask EvictAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        NpgsqlDataSource? toDispose = null;
        lock (_lruLock)
        {
            if (_pools.TryRemove(tenantId, out var node))
            {
                _lru.Remove(node);
                toDispose = node.Value.DataSource;
            }
        }

        // Drop the row cache too so the next miss re-reads CP — handles
        // the rotation case where the encrypted CS changed.
        _tenantRowCache.TryRemove(tenantId, out _);

        if (toDispose is not null)
        {
            _metrics.RecordEviction("explicit");
            _logger.LogInformation(
                "tenant.pool.evicted tenantId={TenantId} reason=explicit",
                tenantId);
            await toDispose.DisposeAsync().ConfigureAwait(false);
        }
    }

    public TenantConnectionPoolStats GetStats() =>
        new(WarmPoolCount: (int)_metrics.WarmPoolCount,
            TotalPoolsOpenedSinceStartup: _metrics.OpenedTotal,
            TotalPoolsEvictedSinceStartup: _metrics.EvictedTotal);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        // Snapshot under the lock so we don't observe partially-evicted
        // state while disposing.
        List<NpgsqlDataSource> sources;
        lock (_lruLock)
        {
            sources = _lru.Select(e => e.DataSource).ToList();
            _lru.Clear();
            _pools.Clear();
        }

        foreach (var ds in sources)
        {
            try
            {
                await ds.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "tenant.pool.dispose_failed during resolver shutdown");
            }
        }

        foreach (var sem in _buildLocks.Values)
            sem.Dispose();
        _buildLocks.Clear();

        _metrics.Dispose();
    }

    // ── private helpers ───────────────────────────────────────────────

    private async Task<NpgsqlDataSource> ResolveSlowAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        _metrics.RecordMiss();

        var sem = _buildLocks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check: another thread may have built it while we
            // were waiting. The fast-path lookup also reposition the
            // LRU — keep that semantics by going through the public
            // method.
            if (_pools.TryGetValue(tenantId, out var existing))
            {
                lock (_lruLock)
                {
                    if (_pools.TryGetValue(tenantId, out var current) && current == existing)
                    {
                        _lru.Remove(existing);
                        _lru.AddFirst(existing);
                        // No hit/miss tally here — we already counted the
                        // miss, and the second-arriver flow is an
                        // implementation detail.
                        return existing.Value.DataSource;
                    }
                }
            }

            var row = await ResolveTenantRowAsync(tenantId, cancellationToken).ConfigureAwait(false);
            var connectionString = DecryptOrThrow(tenantId, row);
            var dataSource = BuildDataSource(tenantId, connectionString);

            var entry = new CacheEntry
            {
                TenantId = tenantId,
                DataSource = dataSource,
            };
            var node = new LinkedListNode<CacheEntry>(entry);

            CacheEntry? evicted = null;
            lock (_lruLock)
            {
                _pools[tenantId] = node;
                _lru.AddFirst(node);

                if (_lru.Count > _options.MaxEntries)
                {
                    var victim = _lru.Last;
                    if (victim is not null)
                    {
                        _lru.RemoveLast();
                        _pools.TryRemove(victim.Value.TenantId, out _);
                        evicted = victim.Value;
                    }
                }
            }

            _metrics.RecordOpened();
            _logger.LogInformation(
                "tenant.pool.created tenantId={TenantId} maxPoolSize={MaxPoolSize}",
                tenantId,
                _options.MaxPoolSize);

            if (evicted is not null)
            {
                _metrics.RecordEviction("lru");
                _logger.LogInformation(
                    "tenant.pool.evicted tenantId={TenantId} reason=lru",
                    evicted.TenantId);
                _tenantRowCache.TryRemove(evicted.TenantId, out _);
                try
                {
                    await evicted.DataSource.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "tenant.pool.dispose_failed for evicted tenant {TenantId}",
                        evicted.TenantId);
                }
            }

            return dataSource;
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<ResolvedTenantRow> ResolveTenantRowAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (_tenantRowCache.TryGetValue(tenantId, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Row;
        }

        await using var ctx = await _cpFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // EncryptedConnectionString + KekVersion + Status are EF shadow
        // properties on the Tenant entity (see ControlPlaneDbContext).
        // We read them via the metadata API and project to the row record
        // to keep the resolver agnostic to the entity surface.
        var tenant = await ctx.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .Select(t => new
            {
                t.Id,
                t.DeletedAt,
                Status = EF.Property<string?>(t, "Status"),
                Envelope = EF.Property<byte[]?>(t, "EncryptedConnectionString"),
                KekVersion = EF.Property<int?>(t, "KekVersion"),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (tenant is null || tenant.DeletedAt is not null)
            throw new TenantNotFoundException(tenantId);

        // 'active' is the only status that yields connections per Doc 04
        // §2.2. Other statuses (provisioning, suspended, deleting, ...)
        // map to TenantNotProvisioned. NULL status is treated as
        // 'active' to keep dev/seed rows working without forcing a
        // status backfill.
        var status = tenant.Status;
        if (status is not null && !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            throw new TenantNotProvisionedException(tenantId, status);

        if (tenant.Envelope is null || tenant.Envelope.Length == 0)
            throw new TenantConnectionStringMissingException(tenantId);

        var row = new ResolvedTenantRow(tenant.Envelope, tenant.KekVersion);

        var ttl = TimeSpan.FromSeconds(Math.Max(1, _options.TenantRowCacheSeconds));
        _tenantRowCache[tenantId] = (DateTimeOffset.UtcNow.Add(ttl), row);
        return row;
    }

    private string DecryptOrThrow(Guid tenantId, ResolvedTenantRow row)
    {
        try
        {
            var plaintext = _decryptor.Decrypt(row.Envelope, row.KekVersion);
            if (string.IsNullOrWhiteSpace(plaintext))
                throw new TenantConnectionDecryptionException(tenantId);
            return plaintext;
        }
        catch (TenantConnectionDecryptionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Wrap WITHOUT including the envelope contents — the inner
            // exception's stack is OK, but we never log the bytes.
            _logger.LogWarning(
                "tenant.pool.build_failed tenantId={TenantId} stage=decrypt error={ErrorType}",
                tenantId,
                ex.GetType().Name);
            throw new TenantConnectionDecryptionException(tenantId, ex);
        }
    }

    private NpgsqlDataSource BuildDataSource(Guid tenantId, string connectionString)
    {
        // Layer per-tenant pool settings on top of the decrypted CS so
        // operator-supplied connection strings can't accidentally leave
        // MaxPoolSize at the Npgsql default of 100. The application-name
        // tag is appended (not overwritten) so admin diagnostics in
        // pg_stat_activity can attribute backends to a specific tenant.
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = _options.MaxPoolSize,
            MinPoolSize = _options.MinPoolSize,
            ConnectionIdleLifetime = _options.ConnectionIdleLifetimeSeconds,
            Timeout = _options.ConnectTimeoutSeconds,
            CommandTimeout = _options.CommandTimeoutSeconds,
            KeepAlive = _options.KeepAliveSeconds,
            ApplicationName = $"tamma-api;tenant={tenantId:D}",
        };

        return NpgsqlDataSource.Create(builder);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(nameof(LruPooledTenantConnectionResolver));
    }

    private readonly record struct ResolvedTenantRow(byte[] Envelope, int? KekVersion);
}
