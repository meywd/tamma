using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Email;

/// <summary>
/// Options bound by <see cref="EmailServiceCollectionExtensions.AddEmailServices"/>.
/// </summary>
public sealed class OutboxSmtpSenderOptions
{
    /// <summary>How often the sender polls for pending messages. Default 5s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Retry backoff schedule. The nth entry applies after the nth failure.
    /// Defaults: 60s → 5m → 30m → 2h → 6h.
    /// </summary>
    public IReadOnlyList<TimeSpan> BackoffSchedule { get; set; } = new[]
    {
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
    };
}

/// <summary>
/// <see cref="BackgroundService"/> that drains the email outbox via
/// <see cref="IEmailOutboxRepository.ClaimNextPendingAsync"/> and delivers each
/// claimed message through an <see cref="ISmtpTransport"/>. Retries use an
/// exponential backoff schedule; the transaction id is the only identifier
/// ever placed in log lines. Event emission:
/// <list type="bullet">
///   <item><description><see cref="EmailEventTypes.Sent"/> on success.</description></item>
///   <item><description><see cref="EmailEventTypes.Failed"/> only when the retry
///     ceiling is reached (the final, permanent failure).</description></item>
/// </list>
/// </summary>
public sealed class OutboxSmtpSender : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly OutboxSmtpSenderOptions _options;
    private readonly IConfiguration _config;
    private readonly ILogger<OutboxSmtpSender> _logger;

    public OutboxSmtpSender(
        IServiceProvider serviceProvider,
        OutboxSmtpSenderOptions options,
        IConfiguration config,
        ILogger<OutboxSmtpSender> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Only run when SMTP is the active provider. The single hosted-service
        // registration covers all three provider modes (smtp / resend /
        // in-memory) so we don't need conditional DI registration.
        var provider = (_config["Email:Provider"] ?? "smtp").Trim().ToLowerInvariant();
        if (provider != "smtp")
        {
            _logger.LogInformation("OutboxSmtpSender disabled (non-smtp provider)");
            return;
        }

        // Allow the poll interval to be overridden by configuration at startup.
        var seconds = _config.GetValue("Email:OutboxPollIntervalSeconds", 0);
        if (seconds > 0)
        {
            _options.PollInterval = TimeSpan.FromSeconds(seconds);
        }

        _logger.LogInformation(
            "OutboxSmtpSender started. Poll interval={Interval}",
            _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OutboxSmtpSender cycle failed");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("OutboxSmtpSender stopped");
    }

    /// <summary>
    /// Claim and deliver a single message, returning <c>true</c> when a row was
    /// processed. Exposed for tests so they don't race the polling timer.
    /// </summary>
    public async Task<bool> ProcessOnceAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IEmailOutboxRepository>();
        var transport = scope.ServiceProvider.GetRequiredService<ISmtpTransport>();
        var events = scope.ServiceProvider.GetRequiredService<IEventRepository>();

        var claimed = await outbox.ClaimNextPendingAsync(DateTime.UtcNow, ct);
        if (claimed is null) return false;

        try
        {
            await transport.SendAsync(claimed, ct);
            await outbox.MarkSentAsync(claimed.Id, ct);
            await EmitSentAsync(events, claimed);

            // Purge the row now that the event store has the permanent audit.
            // Recipient address, subject, and body don't need to persist beyond
            // delivery — EMAIL.SENT.SUCCESS holds txn id + template metadata.
            // Failed rows (MarkFailedAsync → Status=failed) are NOT deleted;
            // operators need them for inspection.
            await outbox.DeleteAsync(claimed.Id, ct);

            _logger.LogInformation("Email delivered txn={TxnId}", claimed.Id);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // NEVER log recipient / subject / body / host — only the txn id.
            var attempt = claimed.Attempts + 1;
            var backoff = PickBackoff(attempt);
            var updated = await outbox.MarkFailedAsync(claimed.Id, ex.Message, backoff, ct);

            if (updated is not null && updated.Status == "failed")
            {
                _logger.LogError(ex,
                    "Email permanently failed txn={TxnId} attempts={Attempts}",
                    claimed.Id, updated.Attempts);
                await EmitFailedAsync(events, claimed, ex);

                // Inbox is a retry buffer only — once retries are exhausted,
                // the audit lives in the event store (EMAIL.SENT.FAILED with
                // txn id + error class). The row carries recipient / subject
                // / body which aren't needed after the terminal outcome, so
                // delete it too.
                await outbox.DeleteAsync(claimed.Id, ct);
            }
            else
            {
                _logger.LogWarning(ex,
                    "Email transient failure txn={TxnId} attempt={Attempt}",
                    claimed.Id, attempt);
            }

            return true;
        }
    }

    private TimeSpan PickBackoff(int attempt)
    {
        if (_options.BackoffSchedule.Count == 0) return TimeSpan.FromMinutes(1);
        var idx = Math.Min(attempt - 1, _options.BackoffSchedule.Count - 1);
        idx = Math.Max(idx, 0);
        return _options.BackoffSchedule[idx];
    }

    private static async Task EmitSentAsync(IEventRepository events, EmailOutboxMessage row)
    {
        var tags = new Dictionary<string, string?>
        {
            ["txn_id"] = row.Id.ToString(),
            ["template"] = row.Template,
            ["tenant_id"] = row.TenantId?.ToString(),
            ["user_id"] = row.UserId?.ToString(),
        };
        var data = new Dictionary<string, object?>
        {
            ["provider"] = "smtp",
            ["attempts"] = row.Attempts + 1,
        };
        await events.AppendAsync(new DomainEvent
        {
            Type = EmailEventTypes.Sent,
            TenantId = row.TenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        });
    }

    private static async Task EmitFailedAsync(
        IEventRepository events, EmailOutboxMessage row, Exception ex)
    {
        var tags = new Dictionary<string, string?>
        {
            ["txn_id"] = row.Id.ToString(),
            ["template"] = row.Template,
            ["tenant_id"] = row.TenantId?.ToString(),
            ["user_id"] = row.UserId?.ToString(),
        };
        var data = new Dictionary<string, object?>
        {
            ["provider"] = "smtp",
            ["error_class"] = ex.GetType().FullName,
        };
        await events.AppendAsync(new DomainEvent
        {
            Type = EmailEventTypes.Failed,
            TenantId = row.TenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        });
    }
}
