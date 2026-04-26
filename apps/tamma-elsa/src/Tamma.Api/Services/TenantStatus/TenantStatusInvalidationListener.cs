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
    }

    /// <summary>Lifetime count of LISTEN connection rebuilds.</summary>
    public long ReconnectCount => Interlocked.Read(ref _reconnectCount);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
        // intentionally let it run on the thread pool. Any exception
        // is observed via ContinueWith → log so we never lose a stack
        // trace to an unobserved task.
        _ = Task.Run(async () =>
        {
            try
            {
                await _resolver.EvictAsync(tenantId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to evict resolver pool for {TenantId} after tenant-status NOTIFY",
                    tenantId);
            }
        });

        _applied.Add(1);

        _logger.LogDebug(
            "Applied tenant-status invalidation for {TenantId}", tenantId);
    }

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }
}
