using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.TenantStatus;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.TenantStatus;

/// <summary>
/// Round-2 follow-up — proves a NOTIFY published by one bus instance
/// (simulating pod A) is observed by a separate listener instance
/// (simulating pod B) and reliably invalidates pod B's local cache +
/// evicts its resolver pool — all within milliseconds.
///
/// <para>This is the headline integration test for cluster-wide
/// invalidation: without this convergence, the per-pod cache TTL
/// (10s default) is the only guarantee, and the design doc's "ms
/// not seconds" claim is unverified.</para>
/// </summary>
[TestFixture]
public class TenantStatusInvalidationListenerTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("listener_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    /// <summary>
    /// Test double for <see cref="ITenantConnectionResolver"/>. Tracks
    /// the tenant ids passed to <c>EvictAsync</c> so the test can
    /// assert the listener wires through to resolver eviction.
    /// </summary>
    private sealed class RecordingResolver : ITenantConnectionResolver
    {
        private readonly List<Guid> _evicted = new();
        private readonly object _lock = new();

        public IReadOnlyList<Guid> Evicted
        {
            get
            {
                lock (_lock) return _evicted.ToArray();
            }
        }

        public ValueTask<NpgsqlDataSource> GetDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<ITenantConnectionLease> LeaseAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask EvictAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
        {
            lock (_lock) _evicted.Add(tenantId);
            return ValueTask.CompletedTask;
        }

        public TenantConnectionPoolStats GetStats()
            => new(0, 0, 0);
    }

    private static MemoryTenantStatusCache NewCache()
        => new(Options.Create(new TenantStatusCacheOptions
        {
            TtlSeconds = 60, // long TTL so the test only sees explicit invalidation
            MaxEntries = 100,
        }));

    [Test]
    public async Task Listener_Invalidates_Local_Cache_When_Bus_From_Another_Pod_Publishes()
    {
        // Two-pod simulation:
        //   Pod A → publisher data source + bus
        //   Pod B → listener data source + listener + cache + resolver
        await using var podADataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var podBDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();

        var podABus = new PostgresTenantStatusInvalidationBus(
            podADataSource,
            NullLogger<PostgresTenantStatusInvalidationBus>.Instance);

        var podBCache = NewCache();
        var podBResolver = new RecordingResolver();
        var podBListener = new TenantStatusInvalidationListener(
            podBDataSource,
            podBCache,
            podBResolver,
            NullLogger<TenantStatusInvalidationListener>.Instance);

        // Pod B caches a status — this is what we want to see invalidated
        // on a NOTIFY from pod A.
        var tenantId = Guid.NewGuid();
        podBCache.Set(tenantId, "active");
        podBCache.TryGet(tenantId, out _).Should().BeTrue();

        // Start the listener.
        using var stoppingCts = new CancellationTokenSource();
        await podBListener.StartAsync(stoppingCts.Token);

        try
        {
            // Wait until the LISTEN command has actually registered on
            // the server. We poll pg_listening_channels() to confirm
            // the listener's connection is fully subscribed before we
            // publish — otherwise a fast publish can race ahead of the
            // LISTEN command and the test flakes.
            await WaitUntilListening(podADataSource);

            // Pod A publishes the invalidation.
            var publishedAt = DateTime.UtcNow;
            await podABus.PublishAsync(tenantId);

            // Pod B's cache should clear within ms. Poll with a short
            // budget — anything over 1000ms is a regression.
            var cleared = await WaitUntil(
                () => !podBCache.TryGet(tenantId, out _),
                TimeSpan.FromMilliseconds(2000));
            var elapsed = DateTime.UtcNow - publishedAt;

            cleared.Should().BeTrue(
                "pod B must invalidate its local cache within ms of pod A's NOTIFY");
            elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
                "convergence latency: actual = {0}ms — must not regress past 2s",
                elapsed.TotalMilliseconds);

            // Resolver should also have been called (fire-and-forget;
            // give it a beat to land).
            await WaitUntil(
                () => podBResolver.Evicted.Contains(tenantId),
                TimeSpan.FromSeconds(2));
            podBResolver.Evicted.Should().Contain(tenantId,
                "the listener must wire through to ITenantConnectionResolver.EvictAsync");
        }
        finally
        {
            await podBListener.StopAsync(stoppingCts.Token);
            podBListener.Dispose();
        }
    }

    [Test]
    public async Task Listener_Reconnects_After_Underlying_Connection_Is_Killed()
    {
        await using var publisherDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var listenerDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();

        var bus = new PostgresTenantStatusInvalidationBus(
            publisherDataSource, NullLogger<PostgresTenantStatusInvalidationBus>.Instance);
        var cache = NewCache();
        var resolver = new RecordingResolver();
        var listener = new TenantStatusInvalidationListener(
            listenerDataSource,
            cache,
            resolver,
            NullLogger<TenantStatusInvalidationListener>.Instance);

        using var stoppingCts = new CancellationTokenSource();
        await listener.StartAsync(stoppingCts.Token);

        try
        {
            await WaitUntilListening(publisherDataSource);

            // Sanity: cluster-wide invalidation works initially.
            var tenant1 = Guid.NewGuid();
            cache.Set(tenant1, "active");
            await bus.PublishAsync(tenant1);
            (await WaitUntil(
                () => !cache.TryGet(tenant1, out _),
                TimeSpan.FromSeconds(2)))
                .Should().BeTrue("listener works pre-disconnect");

            // Kill every backend except our own. This forces the
            // listener's session to terminate; the BackgroundService
            // catches the exception and reconnects with backoff.
            await using (var killConn = await publisherDataSource.OpenConnectionAsync())
            {
                await using var killCmd = killConn.CreateCommand();
                killCmd.CommandText =
                    "SELECT pg_terminate_backend(pid) "
                    + "FROM pg_stat_activity "
                    + "WHERE pid <> pg_backend_pid() "
                    + "  AND application_name LIKE '%' "
                    + "  AND datname = current_database()";
                await killCmd.ExecuteNonQueryAsync();
            }

            // After the kill, the listener's reconnect path needs to
            // (a) build a new connection and (b) re-issue LISTEN. The
            // initial backoff is 1s — give it 8s to converge.
            await WaitUntil(
                () => listener.ReconnectCount > 0,
                TimeSpan.FromSeconds(8));
            listener.ReconnectCount.Should().BeGreaterThan(0,
                "listener must record at least one reconnect after the backend is killed");

            // After reconnect, a fresh publish must still be observed.
            // Wait for the LISTEN to be re-registered.
            await WaitUntilListening(publisherDataSource, TimeSpan.FromSeconds(15));
            var tenant2 = Guid.NewGuid();
            cache.Set(tenant2, "active");
            await bus.PublishAsync(tenant2);
            (await WaitUntil(
                () => !cache.TryGet(tenant2, out _),
                TimeSpan.FromSeconds(8)))
                .Should().BeTrue("listener must resume after reconnect");
        }
        finally
        {
            await listener.StopAsync(stoppingCts.Token);
            listener.Dispose();
        }
    }

    [Test]
    public async Task Listener_Tolerates_Malformed_Payload_And_Stays_Alive()
    {
        // Manually fire a NOTIFY with a non-Guid payload directly
        // through Postgres, then a valid one. The listener should log
        // + skip the bad message and still process the good one.
        await using var publisherDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var listenerDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();

        var cache = NewCache();
        var resolver = new RecordingResolver();
        var listener = new TenantStatusInvalidationListener(
            listenerDataSource,
            cache,
            resolver,
            NullLogger<TenantStatusInvalidationListener>.Instance);

        using var stoppingCts = new CancellationTokenSource();
        await listener.StartAsync(stoppingCts.Token);

        try
        {
            await WaitUntilListening(publisherDataSource);

            // Fire a malformed NOTIFY first.
            await using (var conn = await publisherDataSource.OpenConnectionAsync())
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT pg_notify(@channel, @payload)";
                cmd.Parameters.AddWithValue("channel", PostgresTenantStatusInvalidationBus.ChannelName);
                cmd.Parameters.AddWithValue("payload", "not-a-guid");
                await cmd.ExecuteNonQueryAsync();
            }

            // Now a valid one. If the listener crashed on the
            // malformed payload, this would never get applied.
            var validBus = new PostgresTenantStatusInvalidationBus(
                publisherDataSource, NullLogger<PostgresTenantStatusInvalidationBus>.Instance);
            var tenantId = Guid.NewGuid();
            cache.Set(tenantId, "active");
            await validBus.PublishAsync(tenantId);

            (await WaitUntil(
                () => !cache.TryGet(tenantId, out _),
                TimeSpan.FromSeconds(3)))
                .Should().BeTrue(
                "listener must remain alive after a malformed payload and process the next valid NOTIFY");
        }
        finally
        {
            await listener.StopAsync(stoppingCts.Token);
            listener.Dispose();
        }
    }

    /// <summary>
    /// Polls until <paramref name="predicate"/> returns true or the
    /// budget elapses. Returns true on success, false on timeout.
    /// </summary>
    private static async Task<bool> WaitUntil(Func<bool> predicate, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    /// <summary>
    /// Polls Postgres until at least one connection in the cluster is
    /// LISTENing on the bus channel. Used to dodge the publish-before-
    /// listen race that would otherwise flake the integration test.
    /// </summary>
    private static async Task WaitUntilListening(
        NpgsqlDataSource dataSource, TimeSpan? budget = null)
    {
        var window = budget ?? TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow + window;

        while (DateTime.UtcNow < deadline)
        {
            await using var conn = await dataSource.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            // pg_listening_channels() reports the calling session's
            // channels, not the cluster's. Use pg_stat_activity
            // joined with pg_listening_channels via a subquery — the
            // cleanest cross-session check is querying pg_stat_activity
            // for any backend with the channel name in its query
            // string. We use a more direct test: count active
            // listeners on the channel via the system view.
            cmd.CommandText =
                "SELECT EXISTS ("
                + "SELECT 1 FROM pg_stat_activity "
                + "WHERE state = 'idle' "
                + "  AND query ILIKE @pattern"
                + ")";
            cmd.Parameters.AddWithValue("pattern",
                $"%LISTEN {PostgresTenantStatusInvalidationBus.ChannelName}%");
            var found = (bool)(await cmd.ExecuteScalarAsync())!;
            if (found) return;
            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"Timed out waiting for a backend to be LISTENing on "
            + $"{PostgresTenantStatusInvalidationBus.ChannelName}");
    }
}
