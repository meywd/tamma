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

    /// <summary>
    /// PF-C1 — slow resolver double that holds <c>EvictAsync</c> open
    /// until a release signal fires. Lets the test verify the listener
    /// (a) tracks the in-flight task in its drain dictionary, (b)
    /// awaits the task during <see cref="TenantStatusInvalidationListener.StopAsync"/>,
    /// (c) reports the in-flight gauge accurately while the eviction
    /// is in flight, and (d) cancels the eviction via the threaded
    /// stoppingToken when the host shuts down.
    /// </summary>
    private sealed class SlowResolver : ITenantConnectionResolver
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        private readonly List<Guid> _started = new();
        private readonly List<Guid> _completed = new();
        private readonly List<bool> _cancelled = new();

        public IReadOnlyList<Guid> Started
        {
            get { lock (_lock) return _started.ToArray(); }
        }

        public IReadOnlyList<Guid> Completed
        {
            get { lock (_lock) return _completed.ToArray(); }
        }

        public IReadOnlyList<bool> CancelledOutcomes
        {
            get { lock (_lock) return _cancelled.ToArray(); }
        }

        /// <summary>Fire to let queued evictions complete.</summary>
        public void ReleaseAll() => _release.TrySetResult();

        public ValueTask<NpgsqlDataSource> GetDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<ITenantConnectionLease> LeaseAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async ValueTask EvictAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
        {
            lock (_lock) _started.Add(tenantId);

            // Hold here until either the test releases us or the
            // listener cancels via stoppingToken propagation.
            try
            {
                using var reg = cancellationToken.Register(
                    () => _release.TrySetResult());
                await _release.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_lock)
                {
                    _completed.Add(tenantId);
                    _cancelled.Add(cancellationToken.IsCancellationRequested);
                }
            }
        }

        public TenantConnectionPoolStats GetStats() => new(0, 0, 0);
    }

    [Test]
    public async Task StopAsync_Drains_InFlight_Eviction_Tasks_Within_Bounded_Timeout()
    {
        // PF-C1 — host shutdown must wait for fire-and-forget evictions
        // spawned by OnNotification. Without the drain, StopAsync
        // returns while NpgsqlDataSource.DisposeAsync is still racing
        // downstream and we leak Postgres backend slots.
        await using var publisherDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var listenerDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();

        var bus = new PostgresTenantStatusInvalidationBus(
            publisherDataSource, NullLogger<PostgresTenantStatusInvalidationBus>.Instance);
        var cache = NewCache();
        var slow = new SlowResolver();
        var listener = new TenantStatusInvalidationListener(
            listenerDataSource, cache, slow,
            NullLogger<TenantStatusInvalidationListener>.Instance)
        {
            // Generous drain budget — we'll release manually below.
            ShutdownEvictionDrainTimeout = TimeSpan.FromSeconds(5),
        };

        using var stoppingCts = new CancellationTokenSource();
        await listener.StartAsync(stoppingCts.Token);

        try
        {
            await WaitUntilListening(publisherDataSource);

            // Trigger an eviction that won't complete until we say so.
            var tenantId = Guid.NewGuid();
            await bus.PublishAsync(tenantId);

            // The eviction must enter SlowResolver.EvictAsync — i.e. be
            // tracked by the listener's in-flight dictionary.
            (await WaitUntil(
                () => slow.Started.Contains(tenantId),
                TimeSpan.FromSeconds(3)))
                .Should().BeTrue("listener must dispatch the resolver eviction");

            listener.InFlightEvictionCount.Should().BeGreaterThan(0,
                "in-flight gauge must report the active eviction");

            // Release the eviction in parallel — StopAsync should
            // observe the completion via the drain.
            var releaseTask = Task.Run(async () =>
            {
                await Task.Delay(250);
                slow.ReleaseAll();
            });

            await listener.StopAsync(stoppingCts.Token);
            await releaseTask;

            // After StopAsync returns, the eviction must have actually
            // completed (not just been signalled). This is the crux of
            // the drain — without it, the eviction would still be
            // pending after StopAsync.
            slow.Completed.Should().Contain(tenantId,
                "StopAsync must await in-flight evictions before returning");
            listener.InFlightEvictionCount.Should().Be(0,
                "in-flight tracker must be empty after StopAsync drains");
        }
        finally
        {
            slow.ReleaseAll();
            listener.Dispose();
        }
    }

    [Test]
    public async Task OnNotification_Threads_StoppingToken_To_Resolver_Eviction()
    {
        // PF-C1 — fire-and-forget evictions used to receive
        // CancellationToken.None, so a host shutdown couldn't
        // cooperatively cancel an in-flight eviction. The fix threads
        // the listener's stoppingToken through. Verify by triggering a
        // shutdown while an eviction is mid-flight: the resolver
        // observes IsCancellationRequested=true.
        await using var publisherDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var listenerDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();

        var bus = new PostgresTenantStatusInvalidationBus(
            publisherDataSource, NullLogger<PostgresTenantStatusInvalidationBus>.Instance);
        var cache = NewCache();
        var slow = new SlowResolver();
        var listener = new TenantStatusInvalidationListener(
            listenerDataSource, cache, slow,
            NullLogger<TenantStatusInvalidationListener>.Instance)
        {
            // Tight drain budget so the test doesn't sit on a wedged
            // eviction — the SlowResolver releases on cancellation
            // anyway.
            ShutdownEvictionDrainTimeout = TimeSpan.FromSeconds(2),
        };

        using var stoppingCts = new CancellationTokenSource();
        await listener.StartAsync(stoppingCts.Token);

        try
        {
            await WaitUntilListening(publisherDataSource);

            var tenantId = Guid.NewGuid();
            await bus.PublishAsync(tenantId);

            (await WaitUntil(
                () => slow.Started.Contains(tenantId),
                TimeSpan.FromSeconds(3)))
                .Should().BeTrue("eviction must reach the resolver");

            // Trigger shutdown WITHOUT releasing the resolver — the
            // stoppingToken must propagate and cooperatively cancel
            // the in-flight eviction.
            await listener.StopAsync(stoppingCts.Token);

            // SlowResolver records IsCancellationRequested at the
            // moment it returns. Must observe true here — proving the
            // listener threaded the stoppingToken (not None) through.
            slow.CancelledOutcomes.Should().Contain(true,
                "the listener must thread its stoppingToken to the eviction so host shutdown propagates cancellation");
        }
        finally
        {
            slow.ReleaseAll();
            listener.Dispose();
        }
    }

    [Test]
    public async Task OnNotification_TracksAndCleansUp_InFlightTasks_Across_Multiple_Notifications()
    {
        // PF-C1 — the in-flight tracker must self-clean as evictions
        // complete. A long-lived listener must not accumulate
        // completed-task references between notifications, or the
        // dictionary becomes a slow leak.
        await using var publisherDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var listenerDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();

        var bus = new PostgresTenantStatusInvalidationBus(
            publisherDataSource, NullLogger<PostgresTenantStatusInvalidationBus>.Instance);
        var cache = NewCache();
        var resolver = new RecordingResolver(); // fast resolver — completes instantly
        var listener = new TenantStatusInvalidationListener(
            listenerDataSource, cache, resolver,
            NullLogger<TenantStatusInvalidationListener>.Instance);

        using var stoppingCts = new CancellationTokenSource();
        await listener.StartAsync(stoppingCts.Token);

        try
        {
            await WaitUntilListening(publisherDataSource);

            // Fire 5 notifications in a row.
            var tenantIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
            foreach (var id in tenantIds)
                await bus.PublishAsync(id);

            // All evictions must converge.
            (await WaitUntil(
                () => tenantIds.All(id => resolver.Evicted.Contains(id)),
                TimeSpan.FromSeconds(5)))
                .Should().BeTrue("all 5 evictions must dispatch through the listener");

            // After all complete, the in-flight tracker must drain to 0.
            // The self-cleaning ContinueWith fires async, so allow a
            // short polling window.
            (await WaitUntil(
                () => listener.InFlightEvictionCount == 0,
                TimeSpan.FromSeconds(2)))
                .Should().BeTrue(
                "the in-flight tracker must self-clean as evictions complete — otherwise long-lived listeners leak completed-task references");
        }
        finally
        {
            await listener.StopAsync(stoppingCts.Token);
            listener.Dispose();
        }
    }

    [Test]
    public async Task StopAsync_With_NoInFlightEvictions_Returns_Immediately()
    {
        // Edge case: StopAsync on a listener that's never seen a
        // notification must not block. The drain path takes a fast
        // exit when the in-flight tracker is empty.
        await using var listenerDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();

        var listener = new TenantStatusInvalidationListener(
            listenerDataSource, NewCache(), new RecordingResolver(),
            NullLogger<TenantStatusInvalidationListener>.Instance);

        using var stoppingCts = new CancellationTokenSource();
        await listener.StartAsync(stoppingCts.Token);

        try
        {
            // No notification fired → no in-flight evictions.
            listener.InFlightEvictionCount.Should().Be(0);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await listener.StopAsync(stoppingCts.Token);
            sw.Stop();

            // Must NOT consume the drain budget.
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
                "with no in-flight evictions, StopAsync must return promptly without consuming the drain budget");
        }
        finally
        {
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
