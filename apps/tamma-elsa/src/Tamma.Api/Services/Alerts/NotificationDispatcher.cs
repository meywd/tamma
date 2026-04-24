using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Alerts;

/// <summary>
/// Options for <see cref="NotificationDispatcher"/>.
/// </summary>
public sealed class NotificationDispatcherOptions
{
    /// <summary>How often the dispatcher polls the delivery-attempt
    /// table. Default <b>10 seconds</b> per the Wave C.1 plan.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Retry backoff schedule. The Nth entry applies after
    /// the Nth failure. After the last entry, the attempt stays in
    /// the <c>failed</c> state permanently.
    ///
    /// <para>Defaults: <c>30s → 2m → 5m → 15m → 30m</c> (1 initial +
    /// 5 retries = 6 attempts total, ~52 minutes window) per Wave
    /// C.1 plan. Note there are 5 inter-attempt delays for 6
    /// attempts. The terminal short-circuit fires when
    /// <c>AttemptNumber &gt;= MaxAttempts</c>, AFTER the post-failure
    /// increment — so to compute the 5th delay (idx=4 → 30m, set when
    /// AttemptNumber=6) the terminal check must NOT fire at 6. That
    /// requires <see cref="MaxAttempts"/> to be at least
    /// <c>BackoffSchedule.Count + 2 = 7</c>.</para></summary>
    public IReadOnlyList<TimeSpan> BackoffSchedule { get; set; } = new[]
    {
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
    };

    /// <summary>Terminal sentinel for retry exhaustion. Default <b>7</b>
    /// — semantically "1 initial + 5 retries + 1 terminal-after slot".
    /// The dispatcher picks rows where <c>AttemptNumber &lt; MaxAttempts</c>
    /// (so the 6th attempt at AttemptNumber=6 IS picked); after the 6th
    /// failure AttemptNumber becomes 7 and the terminal check
    /// <c>AttemptNumber &gt;= MaxAttempts</c> fires, leaving the row
    /// <c>failed</c> permanently for audit. Must be ≥
    /// <c>BackoffSchedule.Count + 2</c> for every backoff entry to be
    /// reachable; with the default 5-entry schedule that means
    /// MaxAttempts ≥ 7.</summary>
    public int MaxAttempts { get; set; } = 7;

    /// <summary>Max rows claimed per poll tick. Default 100 so a backlog
    /// doesn't starve the tick.</summary>
    public int BatchSize { get; set; } = 100;
}

/// <summary>
/// Story 5.6 / 1.5-37 (Wave C.1) — background dispatcher that
/// drains <c>alert_delivery_attempts</c> rows in the
/// <c>pending</c> or <c>failed</c> state whose
/// <c>NextAttemptAt</c> is null-or-past. For each row, it resolves
/// the channel via <see cref="IAlertChannelRegistry"/>, calls
/// <see cref="IAlertChannel.SendAsync"/>, records the outcome, and
/// emits an <c>ALERT.DELIVERY_SUCCESS</c> or
/// <c>ALERT.DELIVERY_FAILED</c> DCB event.
///
/// <para>Retry envelope: exponential backoff per
/// <see cref="NotificationDispatcherOptions.BackoffSchedule"/>; 6
/// attempts total (1 initial + 5 retries), ~52 minutes wall-clock.
/// On the final failure the row stays <c>failed</c> permanently — the
/// dispatcher will skip it on subsequent polls because
/// <c>AttemptNumber &gt;= MaxAttempts</c>.</para>
/// </summary>
public sealed class NotificationDispatcher : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly NotificationDispatcherOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(
        IServiceProvider serviceProvider,
        NotificationDispatcherOptions options,
        TimeProvider timeProvider,
        ILogger<NotificationDispatcher> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "NotificationDispatcher starting — poll every {Interval}s, " +
            "max attempts {MaxAttempts}, batch size {BatchSize}.",
            _options.PollInterval.TotalSeconds,
            _options.MaxAttempts, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "NotificationDispatcher tick threw; continuing.");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("NotificationDispatcher shut down.");
    }

    /// <summary>
    /// Run a single dispatch tick. Exposed as public for tests so
    /// they can exercise the dispatcher deterministically without
    /// driving <see cref="ExecuteAsync(CancellationToken)"/>.
    /// </summary>
    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<ControlPlaneDbContext>();
        var registry = scope.ServiceProvider
            .GetRequiredService<IAlertChannelRegistry>();
        var events = scope.ServiceProvider
            .GetRequiredService<IEventRepository>();

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Claim up to BatchSize eligible rows. Eligible =
        //   status IN ('pending','failed')
        //   AND attempt_number <= MaxAttempts - 1 (so a fresh attempt
        //     can still succeed; the row is final after the MaxAttempts'th
        //     failure writes)
        //   AND (NextAttemptAt IS NULL OR NextAttemptAt <= now)
        var eligible = await db.AlertDeliveryAttempts
            .Where(a =>
                (a.Status == AlertDeliveryStatus.Pending
                 || a.Status == AlertDeliveryStatus.Failed)
                && a.AttemptNumber < _options.MaxAttempts
                && (a.NextAttemptAt == null || a.NextAttemptAt <= now))
            .OrderBy(a => a.CreatedAt)
            .Take(_options.BatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (eligible.Count == 0)
            return 0;

        var processed = 0;
        foreach (var attempt in eligible)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                await DeliverAndRecordAsync(
                        db, registry, events, attempt, ct)
                    .ConfigureAwait(false);
                processed++;
            }
            catch (Exception ex)
            {
                // A row-level crash must not take down the batch —
                // record the failure against the attempt and move on.
                _logger.LogError(ex,
                    "Row-level dispatch crash for attemptId {AttemptId}.",
                    attempt.Id);
                await RecordFailureAsync(
                        db, events, attempt,
                        $"Unexpected exception: {ex.GetType().Name}: {ex.Message}",
                        ct)
                    .ConfigureAwait(false);
                processed++;
            }
        }

        return processed;
    }

    private async Task DeliverAndRecordAsync(
        ControlPlaneDbContext db,
        IAlertChannelRegistry registry,
        IEventRepository events,
        AlertDeliveryAttempt attempt,
        CancellationToken ct)
    {
        var alert = await db.Alerts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == attempt.AlertId, ct)
            .ConfigureAwait(false);
        var channel = await db.AlertChannels
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == attempt.ChannelId, ct)
            .ConfigureAwait(false);

        if (alert is null || channel is null)
        {
            await RecordFailureAsync(db, events, attempt,
                    alert is null
                        ? "Parent alert row missing."
                        : "Channel row missing.",
                    ct)
                .ConfigureAwait(false);
            return;
        }

        // Skip delivery if the channel has since been disabled. We
        // still mark the attempt as terminal (failed) so the
        // dispatcher doesn't spin on it forever.
        if (!channel.IsEnabled)
        {
            await RecordFailureAsync(db, events, attempt,
                    "Channel disabled.", ct)
                .ConfigureAwait(false);
            return;
        }

        var impl = registry.Resolve(channel.ChannelType);
        if (impl is null)
        {
            await RecordFailureAsync(db, events, attempt,
                    $"No IAlertChannel registered for '{channel.ChannelType}'.",
                    ct)
                .ConfigureAwait(false);
            return;
        }

        DeliveryResult result;
        try
        {
            result = await impl.SendAsync(alert, channel, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new DeliveryResult(
                Success: false,
                Error: $"Channel threw: {ex.GetType().Name}: {ex.Message}");
        }

        if (result.Success)
        {
            await RecordSuccessAsync(db, events, attempt, alert, channel, ct)
                .ConfigureAwait(false);
        }
        else
        {
            await RecordFailureAsync(
                    db, events, attempt, result.Error ?? "unspecified", ct)
                .ConfigureAwait(false);
        }
    }

    private async Task RecordSuccessAsync(
        ControlPlaneDbContext db,
        IEventRepository events,
        AlertDeliveryAttempt attempt,
        Alert alert,
        AlertChannel channel,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var tracked = await db.AlertDeliveryAttempts
            .FirstAsync(a => a.Id == attempt.Id, ct).ConfigureAwait(false);
        tracked.Status = AlertDeliveryStatus.Success;
        tracked.DeliveredAt = now;
        tracked.Error = null;
        tracked.NextAttemptAt = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await TryEmitAsync(events, new DomainEvent
        {
            Type = AlertEventTypes.DeliverySuccess,
            TenantId = alert.TenantId,
            Tags = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["alertId"] = alert.Id.ToString("N"),
                ["channelId"] = channel.Id.ToString("N"),
                ["channelType"] = channel.ChannelType,
                ["attempt"] = attempt.AttemptNumber.ToString(),
            }),
            Metadata = """{"eventSource":"system"}""",
            Data = "{}",
        }).ConfigureAwait(false);
    }

    private async Task RecordFailureAsync(
        ControlPlaneDbContext db,
        IEventRepository events,
        AlertDeliveryAttempt attempt,
        string error,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var tracked = await db.AlertDeliveryAttempts
            .FirstAsync(a => a.Id == attempt.Id, ct).ConfigureAwait(false);
        tracked.Status = AlertDeliveryStatus.Failed;
        tracked.Error = Truncate(error, 2000);
        tracked.AttemptNumber += 1;

        // Schedule next attempt per the backoff envelope. When the
        // counter is at (or past) MaxAttempts the row stays in
        // failed state — NextAttemptAt stays far in the future (MaxValue)
        // so the poll query skips it forever.
        tracked.NextAttemptAt = ComputeNextAttempt(
            tracked.AttemptNumber, now);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Fetch parent alert / channel only to enrich the event tags.
        // If either is gone at this point (tenant purge mid-delivery)
        // we skip the tag enrichment; the event still fires so the
        // DLQ state is auditable.
        var alert = await db.Alerts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == attempt.AlertId, ct)
            .ConfigureAwait(false);
        var channel = await db.AlertChannels
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == attempt.ChannelId, ct)
            .ConfigureAwait(false);

        await TryEmitAsync(events, new DomainEvent
        {
            Type = AlertEventTypes.DeliveryFailed,
            TenantId = alert?.TenantId,
            Tags = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["alertId"] = attempt.AlertId.ToString("N"),
                ["channelId"] = attempt.ChannelId.ToString("N"),
                ["channelType"] = channel?.ChannelType,
                ["attempt"] = tracked.AttemptNumber.ToString(),
                ["terminal"] = (tracked.AttemptNumber >= _options.MaxAttempts).ToString(),
            }),
            Metadata = """{"eventSource":"system"}""",
            Data = JsonSerializer.Serialize(new { error = Truncate(error, 512) }),
        }).ConfigureAwait(false);
    }

    private DateTime? ComputeNextAttempt(int attemptNumber, DateTime now)
    {
        // Row convention: AttemptNumber starts at 1 when the row is
        // first written (meaning "attempt 1 is what we're trying").
        // After attempt N fails, the caller increments to N+1 and
        // calls this method. The delay before attempt N+1 is
        // BackoffSchedule[N-1] — i.e. the first failure (row now at
        // AttemptNumber=2) uses BackoffSchedule[0] = 30s.
        //
        // We also short-circuit when the incremented counter has
        // reached the ceiling: no more attempts, pin NextAttemptAt
        // to MaxValue so the poll query skips forever.
        if (attemptNumber >= _options.MaxAttempts)
        {
            return DateTime.MaxValue;
        }
        var idx = attemptNumber - 2;
        if (idx < 0 || idx >= _options.BackoffSchedule.Count)
        {
            return DateTime.MaxValue;
        }
        return now + _options.BackoffSchedule[idx];
    }

    private async Task TryEmitAsync(IEventRepository events, DomainEvent evt)
    {
        try
        {
            await events.AppendAsync(evt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Event emission for {Type} failed; continuing.",
                evt.Type);
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
