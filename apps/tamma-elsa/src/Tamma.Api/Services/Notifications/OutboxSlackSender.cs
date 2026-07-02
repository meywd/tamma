using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tamma.Core.Interfaces;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Notifications;

/// <summary>
/// Options bound by <c>NotificationServiceCollectionExtensions.AddSlackNotificationServices</c>.
/// Mirrors <c>OutboxSmtpSenderOptions</c>.
/// </summary>
public sealed class OutboxSlackSenderOptions
{
    /// <summary>How often the sender polls for pending rows. Default 5s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Retry backoff schedule; the nth entry applies after the nth failure.</summary>
    public IReadOnlyList<TimeSpan> BackoffSchedule { get; set; } = new[]
    {
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
    };

    /// <summary>
    /// When <c>true</c> (default) the sender starts its poll loop. Tests that assert
    /// outbox-row state opt out so the loop doesn't race the assertion (mirrors
    /// <c>OutboxSmtpSenderOptions.RunOnStartup</c>).
    /// </summary>
    public bool RunOnStartup { get; set; } = true;

    /// <summary>
    /// Lease timeout for the durability reaper. A row claimed into <c>sending</c>
    /// whose <c>UpdatedAt</c> is older than this is assumed orphaned by a crashed
    /// sender and reset to <c>pending</c> so it is re-delivered (at-least-once).
    /// The reap runs at most once per this interval (throttled in the poll loop),
    /// so a stuck row is re-delivered within ~2× this window in the worst case.
    /// Default 5 minutes — comfortably above a single delivery's duration.
    /// </summary>
    public TimeSpan SendingLeaseTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Story 38-3 (Epic 38, Class D) — the out-of-band Slack notification sender.
/// The fire-and-forget analogue of <c>OutboxSmtpSender</c> and the SOLE holder of
/// the Slack webhook credential in the platform: it drains <c>slack_outbox</c> via
/// <see cref="ISlackOutboxRepository.ClaimNextPendingAsync"/> and performs the
/// transport through the existing <see cref="ISlackIntegrationService"/> (which
/// POSTs to <c>Slack:WebhookUrl</c>). Terminal outcomes are audited to
/// <c>platform_events</c> via <see cref="IPlatformEventRepository"/>:
/// <list type="bullet">
///   <item><description><see cref="NotificationSlackEventTypes.Sent"/> on delivery.</description></item>
///   <item><description><see cref="NotificationSlackEventTypes.Failed"/> on each
///     failed attempt (transient-with-backoff or terminal).</description></item>
/// </list>
///
/// <para><b>Credential safety (load-bearing):</b> the webhook URL/secret is read
/// only by <see cref="ISlackIntegrationService"/> (never by this sender), the
/// message body is not copied into event payloads, and <see cref="Redact"/> strips
/// the configured webhook URL from any error string before it is stored / logged /
/// audited.</para>
/// </summary>
public sealed class OutboxSlackSender : BackgroundService
{
    private const int MaxErrorLength = 500;

    private readonly IServiceProvider _serviceProvider;
    private readonly OutboxSlackSenderOptions _options;
    private readonly IConfiguration _config;
    private readonly ILogger<OutboxSlackSender> _logger;

    // Durability reaper throttle — the reclaim UPDATE matches 0 rows in steady
    // state, so it runs at most once per SendingLeaseTimeout rather than every
    // poll. MinValue means "reap on the first cycle" so a restart immediately
    // recovers rows a prior crash orphaned in 'sending'.
    private DateTime _lastReclaimUtc = DateTime.MinValue;

    public OutboxSlackSender(
        IServiceProvider serviceProvider,
        OutboxSlackSenderOptions options,
        IConfiguration config,
        ILogger<OutboxSlackSender> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Test gate — tests that assert outbox-row state shouldn't race the loop.
        if (!_options.RunOnStartup)
        {
            _logger.LogDebug("OutboxSlackSender gated off (RunOnStartup=false); skipping poll loop.");
            return;
        }

        // The webhook is the transport credential — with none configured there is
        // nothing to deliver, so the sender stays idle (enqueue still works; rows
        // pile up until a webhook is configured and the process restarts).
        if (string.IsNullOrWhiteSpace(_config["Slack:WebhookUrl"]))
        {
            _logger.LogInformation("OutboxSlackSender disabled (Slack:WebhookUrl not configured)");
            return;
        }

        var seconds = _config.GetValue("Slack:OutboxPollIntervalSeconds", 0);
        if (seconds > 0)
        {
            _options.PollInterval = TimeSpan.FromSeconds(seconds);
        }

        var leaseSeconds = _config.GetValue("Slack:OutboxSendingLeaseSeconds", 0);
        if (leaseSeconds > 0)
        {
            _options.SendingLeaseTimeout = TimeSpan.FromSeconds(leaseSeconds);
        }

        _logger.LogInformation("OutboxSlackSender started. Poll interval={Interval}", _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OutboxSlackSender cycle failed");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("OutboxSlackSender stopped");
    }

    /// <summary>
    /// Claim and deliver a single pending row, returning <c>true</c> when a row was
    /// processed. Exposed for tests so they don't race the polling timer.
    /// </summary>
    public async Task<bool> ProcessOnceAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<ISlackOutboxRepository>();
        var slack = scope.ServiceProvider.GetRequiredService<ISlackIntegrationService>();
        var events = scope.ServiceProvider.GetService<IPlatformEventRepository>();

        var now = DateTime.UtcNow;

        // Durability reaper (throttled) — recover rows a crashed sender left
        // orphaned in 'sending' back to 'pending' before we claim the next
        // batch, so at-least-once delivery holds across process restarts.
        await MaybeReclaimAsync(outbox, now, ct).ConfigureAwait(false);

        var claimed = await outbox.ClaimNextPendingAsync(now, ct).ConfigureAwait(false);
        if (claimed is null) return false;

        try
        {
            var error = await PostAsync(slack, claimed, ct).ConfigureAwait(false);
            if (error is null)
            {
                await outbox.MarkSentAsync(claimed.Id, ct).ConfigureAwait(false);
                if (events is not null) await EmitSentAsync(events, claimed, ct).ConfigureAwait(false);

                // Purge the row now the durable audit is in platform_events — the
                // formatted body must not linger past delivery (unbounded growth +
                // content retention). Mirrors OutboxSmtpSender's delete-on-success.
                await outbox.DeleteAsync(claimed.Id, ct).ConfigureAwait(false);

                _logger.LogInformation(
                    "Slack notification delivered outboxId={OutboxId} messageType={MessageType}",
                    claimed.Id, claimed.MessageType);
            }
            else
            {
                await HandleFailureAsync(outbox, events, claimed, error, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await HandleFailureAsync(outbox, events, claimed, Redact(ex.Message), ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Run the durability reaper at most once per <see cref="OutboxSlackSenderOptions.SendingLeaseTimeout"/>.
    /// Resets rows a crashed sender orphaned in <c>sending</c> (UpdatedAt older
    /// than the lease) back to <c>pending</c> so the very next
    /// <see cref="ISlackOutboxRepository.ClaimNextPendingAsync"/> re-delivers
    /// them. Folded into the existing poll loop so no extra hosted service is
    /// needed. Reap failures are logged and swallowed — the claim path still runs.
    /// </summary>
    private async Task MaybeReclaimAsync(
        ISlackOutboxRepository outbox, DateTime now, CancellationToken ct)
    {
        if (now - _lastReclaimUtc < _options.SendingLeaseTimeout) return;
        _lastReclaimUtc = now;

        try
        {
            var reclaimed = await outbox
                .ReclaimStuckSendingAsync(now, _options.SendingLeaseTimeout, ct)
                .ConfigureAwait(false);
            if (reclaimed > 0)
            {
                _logger.LogWarning(
                    "Reclaimed {Count} Slack outbox row(s) stuck in 'sending' past the {Lease} lease",
                    reclaimed, _options.SendingLeaseTimeout);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Slack outbox reclaim pass failed");
        }
    }

    /// <summary>
    /// Perform the actual Slack transport for a claimed row. Each row is a SINGLE
    /// target — a channel XOR a DM (a both-targets intent is split into two rows at
    /// enqueue, so one leg's retry never re-sends the other). A channel post routes to
    /// the channel; a DM opens a direct message. Returns <c>null</c> on success, or a
    /// key-free error string on failure.
    /// </summary>
    private static async Task<string?> PostAsync(
        ISlackIntegrationService slack, SlackOutboxMessage row, CancellationToken ct)
    {
        _ = ct; // ISlackIntegrationService does not take a token today; kept for signature symmetry.

        if (!string.IsNullOrWhiteSpace(row.Channel))
        {
            var post = await slack.SendSlackMessageAsync(row.Channel!, row.Body).ConfigureAwait(false);
            return post.Success ? null : (post.Error ?? "slack channel post failed");
        }

        if (!string.IsNullOrWhiteSpace(row.TargetUserId))
        {
            var dm = await slack.SendSlackDirectMessageAsync(row.TargetUserId!, row.Body).ConfigureAwait(false);
            return dm.Success ? null : (dm.Error ?? "slack dm failed");
        }

        return "no channel or target user on the outbox row";
    }

    private async Task HandleFailureAsync(
        ISlackOutboxRepository outbox,
        IPlatformEventRepository? events,
        SlackOutboxMessage row,
        string error,
        CancellationToken ct)
    {
        var safeError = Redact(error);
        var attempt = row.Attempts + 1;
        var backoff = PickBackoff(attempt);
        var updated = await outbox.MarkFailedAsync(row.Id, safeError, backoff, ct).ConfigureAwait(false);

        var terminal = updated is not null && updated.Status == "failed";
        if (terminal)
        {
            _logger.LogError(
                "Slack notification permanently failed outboxId={OutboxId} attempts={Attempts}",
                row.Id, updated!.Attempts);
        }
        else
        {
            _logger.LogWarning(
                "Slack notification transient failure outboxId={OutboxId} attempt={Attempt}",
                row.Id, attempt);
        }

        if (events is not null)
        {
            await EmitFailedAsync(events, row, safeError, terminal, ct).ConfigureAwait(false);
        }

        if (terminal)
        {
            // Retry buffer exhausted — the audit lives in platform_events
            // (NotificationSlackEventTypes.Failed, terminal=true). The row's body must
            // not linger, so delete it. Mirrors OutboxSmtpSender's delete-on-terminal.
            await outbox.DeleteAsync(row.Id, ct).ConfigureAwait(false);
        }
    }

    private TimeSpan PickBackoff(int attempt)
    {
        if (_options.BackoffSchedule.Count == 0) return TimeSpan.FromMinutes(1);
        var idx = Math.Min(attempt - 1, _options.BackoffSchedule.Count - 1);
        idx = Math.Max(idx, 0);
        return _options.BackoffSchedule[idx];
    }

    /// <summary>
    /// Strip the configured Slack webhook URL from an error string and length-bound
    /// it — the row / event / log must never carry the webhook secret.
    /// </summary>
    private string Redact(string? error)
    {
        if (string.IsNullOrEmpty(error)) return string.Empty;
        var webhook = _config["Slack:WebhookUrl"];
        var cleaned = error;
        if (!string.IsNullOrWhiteSpace(webhook))
        {
            cleaned = cleaned.Replace(webhook, "[redacted-webhook]", StringComparison.OrdinalIgnoreCase);
        }
        return cleaned.Length > MaxErrorLength ? cleaned[..MaxErrorLength] : cleaned;
    }

    private static Task EmitSentAsync(
        IPlatformEventRepository events, SlackOutboxMessage row, CancellationToken ct)
    {
        var data = new Dictionary<string, object?>
        {
            ["provider"] = "slack-webhook",
            ["attempts"] = row.Attempts + 1,
            ["bodyLength"] = row.Body?.Length ?? 0,
        };
        return AppendAsync(events, NotificationSlackEventTypes.Sent, row, data, ct);
    }

    private static Task EmitFailedAsync(
        IPlatformEventRepository events, SlackOutboxMessage row, string safeError, bool terminal, CancellationToken ct)
    {
        var data = new Dictionary<string, object?>
        {
            ["provider"] = "slack-webhook",
            ["attempts"] = row.Attempts + 1,
            ["terminal"] = terminal,
            ["failureReason"] = safeError,
        };
        return AppendAsync(events, NotificationSlackEventTypes.Failed, row, data, ct);
    }

    private static async Task AppendAsync(
        IPlatformEventRepository events,
        string type,
        SlackOutboxMessage row,
        Dictionary<string, object?> data,
        CancellationToken ct)
    {
        var tags = new Dictionary<string, string?>
        {
            ["outbox_id"] = row.Id.ToString(),
            ["message_type"] = row.MessageType,
            ["channel"] = row.Channel,
            ["target_user"] = row.TargetUserId,
            ["tenant_id"] = row.TenantId?.ToString(),
            ["user_id"] = row.UserId?.ToString(),
            ["scope"] = "platform",
        };

        await events.AppendAsync(new PlatformEvent
        {
            Type = type,
            TenantId = row.TenantId,
            UserId = row.UserId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        }, ct).ConfigureAwait(false);
    }
}
