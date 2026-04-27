using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Api.Services.TenantStatus;

/// <summary>
/// Round-2 follow-up — cluster-wide tenant-status cache invalidation
/// subscriber. Pairs with
/// <see cref="PostgresTenantStatusInvalidationBus"/> on the publish
/// side: every pod runs this <see cref="BackgroundService"/>, holds a
/// long-lived <c>NpgsqlConnection</c> open against the control-plane
/// database with <c>LISTEN tamma_tenant_status_changed</c> active, and
/// dispatches local-cache + resolver-pool evictions when a notification
/// arrives.
///
/// <para>The publishing pod also receives its own NOTIFY (Postgres
/// fans the message back to every active LISTENer including the
/// originating session). That's fine: re-invalidating an already-evicted
/// entry is a cheap no-op, and avoiding self-delivery would require
/// payload tagging that doesn't pull weight here.</para>
///
/// <para><b>Resilience</b>: the listen loop wraps the connection +
/// <see cref="NpgsqlConnection.WaitAsync(System.Threading.CancellationToken)"/>
/// call in a try/catch with exponential backoff (1s → 2s → 4s, capped
/// at 30s). A dropped connection / Postgres restart is logged at WARN,
/// the connection torn down, and a fresh listen connection opened on
/// the next backoff tick. <see cref="ReconnectCount"/> exposes the
/// running counter for diagnostics.</para>
///
/// <para><b>Lifetime</b>: extends <see cref="BackgroundService"/> so
/// the host runtime threads <c>stoppingToken</c> through to
/// <see cref="ExecuteAsync"/> and shutdown unwinds cleanly. The Npgsql
/// <c>WaitAsync</c> call is fully cancellation-aware, so cancelling the
/// token interrupts the wait without leaving the connection in a bad
/// state.</para>
/// </summary>
public sealed class TenantStatusInvalidationListener : BackgroundService
{
    /// <summary>
    /// Public meter name. Used by tests to subscribe a
    /// <see cref="MeterListener"/> and assert that the
    /// <c>tenant_status_invalidation.received</c> /
    /// <c>tenant_status_invalidation.applied</c> counters are bumped.
    /// </summary>
    public const string MeterName = "Tamma.TenantStatusInvalidation";

    private readonly NpgsqlDataSource _dataSource;
    private readonly ITenantStatusCache _cache;
    private readonly ITenantConnectionResolver _resolver;
    private readonly ILogger<TenantStatusInvalidationListener> _logger;

    private readonly Meter _meter;
    private readonly Counter<long> _received;
    private readonly Counter<long> _applied;
    private readonly Counter<long> _reconnects;

    private long _reconnectCount;

    /// <summary>
    /// PF-C1 — outstanding fire-and-forget eviction tasks spawned by
    /// <see cref="OnNotification"/>. The notification callback is sync
    /// (Npgsql contract) but the resolver eviction is async, so the
    /// callback can't await — it would block the connection's
    /// notification thread. Instead we track each in-flight task here
    /// keyed by tenant id and drain the dictionary in
    /// <see cref="StopAsync"/> with a bounded timeout. Without this
    /// drain, host shutdown can race in-flight pool evictions and leak
    /// Npgsql backend slots — same shape as the
    /// <c>LruPooledTenantConnectionResolver._pendingDisposes</c> drain
    /// pattern.
    ///
    /// <para>Concurrent NOTIFY arrivals for the same tenant id collapse
    /// into one tracked task: a follow-up notification overwrites the
    /// first slot if the prior task hasn't completed yet. Two evictions
    /// for the same tenant racing is a no-op idempotency-wise (resolver
    /// <c>EvictAsync</c> is idempotent), and the lost reference doesn't
    /// matter at shutdown because both tasks reach the same terminal
    /// state. We use the dictionary keyed by Guid (rather than a flat
    /// list) so the in-flight count metric is meaningful at the
    /// per-tenant granularity ops dashboards care about.</para>
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Task> _inFlightEvictions = new();

    /// <summary>
    /// PF-C1 — the stopping token threaded through to fire-and-forget
    /// resolver eviction tasks. Captured when <see cref="ExecuteAsync"/>
    /// is invoked by the host and read by <see cref="OnNotification"/>
    /// (which has no CT of its own — Npgsql's notification handler
    /// signature is sync void). Initialised to <c>None</c> so a
    /// notification arriving before the listen loop attaches the
    /// handler still degrades gracefully (cancellation just doesn't
    /// fire — the eviction runs to completion).
    /// </summary>
    private CancellationToken _stoppingToken = CancellationToken.None;

    /// <summary>
    /// PF-C1 — bounded shutdown drain budget. Long enough that healthy
    /// resolver pools complete a few <c>EvictAsync</c> calls; short
    /// enough that a wedged eviction doesn't block process teardown
    /// indefinitely. Mirrors
    /// <c>LruPooledTenantConnectionResolver.ShutdownDeferredDisposeTimeout</c>.
    /// Internal so unit tests can shorten it without touching options
    /// plumbing.
    /// </summary>
    internal TimeSpan ShutdownEvictionDrainTimeout { get; init; } = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan _initialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _maxBackoff = TimeSpan.FromSeconds(30);

    public TenantStatusInvalidationListener(
        NpgsqlDataSource dataSource,
        ITenantStatusCache cache,
        ITenantConnectionResolver resolver,
        ILogger<TenantStatusInvalidationListener> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(logger);
        _dataSource = dataSource;
        _cache = cache;
        _resolver = resolver;
        _logger = logger;

        _meter = new Meter(MeterName, "1.0.0");
        _received = _meter.CreateCounter<long>(
            "tenant_status_invalidation.received",
            unit: "{notification}",
            description: "Tenant-status NOTIFY messages received from Postgres LISTEN/NOTIFY.");
        _applied = _meter.CreateCounter<long>(
            "tenant_status_invalidation.applied",
            unit: "{notification}",
            description: "Tenant-status NOTIFY messages successfully parsed + dispatched to the local cache + resolver.");
        _reconnects = _meter.CreateCounter<long>(
            "tenant_status_invalidation.reconnects",
            unit: "{reconnect}",
            description: "Times the LISTEN connection was rebuilt after a failure.");

        // PF-C1 — observable gauge over the in-flight eviction tracker.
        // Ops dashboards key on this when investigating shutdown-stuck
        // pods: a non-zero gauge during a graceful drain means the
        // resolver pool is taking longer than expected to evict.
        _meter.CreateObservableGauge(
            "tenant_status_invalidation.in_flight_evictions",
            () => (long)_inFlightEvictions.Count,
            unit: "{eviction}",
            description: "Outstanding fire-and-forget resolver evictions spawned by tenant-status NOTIFY callbacks. Drained on host shutdown.");
    }

    /// <summary>Lifetime count of LISTEN connection rebuilds.</summary>
    public long ReconnectCount => Interlocked.Read(ref _reconnectCount);

    /// <summary>
    /// PF-C1 — current in-flight fire-and-forget resolver-eviction task
    /// count. Exposed for tests + diagnostics. Should be 0 when the
    /// listener is idle and at the end of <see cref="StopAsync"/>.
    /// </summary>
    public int InFlightEvictionCount => _inFlightEvictions.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PF-C1 — capture the host's stopping token so OnNotification
        // (sync Npgsql callback, no CT in signature) can thread it
        // through to fire-and-forget resolver evictions. Set BEFORE the
        // listen loop opens so a fast-arriving notification doesn't see
        // CT.None.
        _stoppingToken = stoppingToken;

        _logger.LogInformation(
            "TenantStatusInvalidationListener starting; channel={Channel}",
            PostgresTenantStatusInvalidationBus.ChannelName);

        var backoff = _initialBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenLoopAsync(stoppingToken).ConfigureAwait(false);

                // ListenLoopAsync only returns on graceful cancellation.
                // Any unexpected return path falls through to the
                // backoff branch below.
                if (stoppingToken.IsCancellationRequested)
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown — exit cleanly.
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _reconnectCount);
                _reconnects.Add(1);
                _logger.LogWarning(
                    ex,
                    "TenantStatusInvalidationListener disconnected; reconnecting in {BackoffSeconds}s "
                    + "(reconnect #{ReconnectCount})",
                    backoff.TotalSeconds, ReconnectCount);
            }

            try
            {
                await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Exponential backoff capped at _maxBackoff.
            backoff = TimeSpan.FromMilliseconds(
                Math.Min(backoff.TotalMilliseconds * 2, _maxBackoff.TotalMilliseconds));
        }

        _logger.LogInformation("TenantStatusInvalidationListener stopped.");
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        // Hold the connection open for the lifetime of the listen loop.
        // The data source's pool will give us a fresh physical
        // connection — once we attach the Notification handler and
        // issue LISTEN, that connection is dedicated to receiving
        // notifications until the loop exits or the connection drops.
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        conn.Notification += OnNotification;

        try
        {
            await using (var cmd = conn.CreateCommand())
            {
                // Channel identifiers can't be parameterised in LISTEN
                // syntax. We control the constant statically (no user
                // input) so this is safe — no SQL-injection surface.
                cmd.CommandText = $"LISTEN {PostgresTenantStatusInvalidationBus.ChannelName}";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "TenantStatusInvalidationListener LISTENing on {Channel}",
                PostgresTenantStatusInvalidationBus.ChannelName);

            // Wait for notifications until the host shuts us down or
            // the connection drops. WaitAsync returns void-Task that
            // only completes via cancellation OR an underlying
            // connection error — the latter throws and bubbles up to
            // ExecuteAsync's catch + backoff.
            while (!ct.IsCancellationRequested)
            {
                await conn.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            conn.Notification -= OnNotification;
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
    {
        _received.Add(1);

        if (!string.Equals(
                e.Channel,
                PostgresTenantStatusInvalidationBus.ChannelName,
                StringComparison.Ordinal))
        {
            // Different channel — should never happen since we only
            // LISTEN on one. Defensive log + skip.
            _logger.LogDebug(
                "Ignoring notification on unexpected channel {Channel}", e.Channel);
            return;
        }

        var payload = e.Payload;
        if (string.IsNullOrEmpty(payload))
        {
            _logger.LogWarning(
                "Received tenant-status invalidation NOTIFY with empty payload on {Channel}",
                e.Channel);
            return;
        }

        if (!Guid.TryParse(payload, out var tenantId))
        {
            _logger.LogWarning(
                "Received tenant-status invalidation NOTIFY with malformed payload {Payload} on {Channel}",
                payload, e.Channel);
            return;
        }

        // Best-effort dispatch. The cache invalidate is sync + cheap;
        // the resolver eviction is async + may dispose a warm pool.
        // We swallow any failure inside the dispatch so a
        // mis-behaving resolver never tears down the listen loop.
        try
        {
            _cache.Invalidate(tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to invalidate tenant-status cache for {TenantId}", tenantId);
        }

        // Fire-and-forget resolver eviction. Awaiting here would block
        // the Notification handler thread the connection is using; we
        // intentionally let it run on the thread pool.
        //
        // PF-C1 — we cannot await on this hot path, but we MUST track
        // the spawned task so host shutdown can drain it. Without the
        // tracker, StopAsync returns before in-flight evictions
        // complete, races NpgsqlDataSource.DisposeAsync downstream, and
        // can leak Postgres backend slots.
        //
        // The eviction also receives the listener's stoppingToken
        // (captured in ExecuteAsync) instead of CancellationToken.None,
        // so a host shutdown signal cooperatively cancels the eviction
        // rather than just waiting on it. The drain in StopAsync gives
        // already-started evictions a bounded budget to complete after
        // cancellation propagates.
        var evictionToken = _stoppingToken;
        var evictionTask = Task.Run(
            () => RunEvictionAsync(tenantId, evictionToken),
            evictionToken);

        // Stash the task in the in-flight tracker. Concurrent NOTIFYs
        // for the same tenant collapse into one slot — the prior task
        // reference is dropped (idempotent eviction means the lost
        // reference still completes harmlessly), but the current
        // pending task is the one StopAsync will await on.
        _inFlightEvictions[tenantId] = evictionTask;

        // Self-cleaning: when the task completes, evict ourselves from
        // the tracker so a long-running listener doesn't accumulate
        // completed-task references in memory between shutdowns.
        // TryRemove (KeyValuePair) only removes if the slot still
        // points at the task we just registered — protects against
        // racing NOTIFYs that re-bind the slot to a fresher task.
        _ = evictionTask.ContinueWith(
            t => _inFlightEvictions.TryRemove(
                new KeyValuePair<Guid, Task>(tenantId, t)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _applied.Add(1);

        _logger.LogDebug(
            "Applied tenant-status invalidation for {TenantId}", tenantId);
    }

    /// <summary>
    /// PF-C1 — body of the fire-and-forget eviction task spawned by
    /// <see cref="OnNotification"/>. Pulled out as a named method so
    /// the lambda capture stays narrow and the exception-handling
    /// envelope is easy to reason about.
    ///
    /// <para>Cancellation handling: <see cref="OperationCanceledException"/>
    /// triggered by host shutdown is logged at DEBUG (expected
    /// teardown signal), not WARN. Any other exception is logged at
    /// WARN with the tenant id for triage.</para>
    /// </summary>
    private async Task RunEvictionAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            await _resolver.EvictAsync(tenantId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Resolver eviction for {TenantId} cancelled during host shutdown",
                tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to evict resolver pool for {TenantId} after tenant-status NOTIFY",
                tenantId);
        }
    }

    /// <summary>
    /// PF-C1 — drain in-flight fire-and-forget resolver evictions on
    /// host shutdown with a bounded timeout. Without this drain, the
    /// host can return from <see cref="BackgroundService.StopAsync"/>
    /// while pool evictions are still racing
    /// <c>NpgsqlDataSource.DisposeAsync</c> on a different code path,
    /// leaking Postgres backend slots.
    ///
    /// <para>Sequence: (1) call base <see cref="BackgroundService.StopAsync"/>
    /// to signal the listen loop to unwind cooperatively;
    /// (2) snapshot the in-flight tracker; (3) await the snapshot with
    /// <see cref="ShutdownEvictionDrainTimeout"/> as the budget;
    /// (4) log a warning if the drain timed out so ops can see which
    /// pod left work behind.</para>
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Step 1: let the base BackgroundService cancel its internal
        // cancellation token, which propagates to ExecuteAsync's
        // listen loop. This also signals the captured _stoppingToken
        // that fire-and-forget evictions are observing.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Step 2: snapshot the in-flight tracker. We snapshot rather
        // than read live because the tracker self-cleans on task
        // completion — a live read would race the cleanup callbacks.
        var pending = _inFlightEvictions.Values.ToArray();
        if (pending.Length == 0)
        {
            _logger.LogInformation(
                "TenantStatusInvalidationListener stop drain — no in-flight evictions");
            return;
        }

        _logger.LogInformation(
            "TenantStatusInvalidationListener stop drain — awaiting {Count} in-flight eviction(s) (timeout={TimeoutSeconds}s)",
            pending.Length,
            (int)ShutdownEvictionDrainTimeout.TotalSeconds);

        // Step 3: await the snapshot with a bounded budget. We use a
        // local CTS rather than the caller's cancellationToken so a
        // tight host-shutdown deadline doesn't truncate the drain
        // sooner than our own budget — cancellationToken still wins if
        // it's tighter.
        try
        {
            using var timeoutCts = new CancellationTokenSource(ShutdownEvictionDrainTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            var allDone = Task.WhenAll(pending);
            var completed = await Task.WhenAny(
                allDone,
                Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token))
                .ConfigureAwait(false);

            if (completed != allDone)
            {
                // Step 4 (degraded): drain timed out. Log + return —
                // host shutdown isn't blocked by an eviction we
                // couldn't drain.
                _logger.LogWarning(
                    "tenant.status_invalidation.shutdown_drain_timeout pendingEvictions={Count} timeoutSeconds={Seconds}",
                    pending.Length,
                    (int)ShutdownEvictionDrainTimeout.TotalSeconds);
            }
            else
            {
                _logger.LogInformation(
                    "TenantStatusInvalidationListener stop drain — all {Count} eviction(s) completed",
                    pending.Length);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: a drain failure must not surface to host
            // shutdown. Log + move on.
            _logger.LogWarning(
                ex,
                "tenant.status_invalidation.shutdown_drain_failed pendingEvictions={Count}",
                pending.Length);
        }
    }

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }
}
