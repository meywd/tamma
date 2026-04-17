using System.Text.Json;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Email;

/// <summary>
/// Outbox-backed <see cref="IEmailService"/>. <see cref="SendAsync"/> does not
/// talk to SMTP — it persists the message in <c>email_outbox</c> and emits a
/// <see cref="EmailEventTypes.Queued"/> event. The actual SMTP delivery is
/// performed asynchronously by <c>OutboxSmtpSender</c>, which also emits
/// <see cref="EmailEventTypes.Sent"/> / <see cref="EmailEventTypes.Failed"/>.
///
/// <para>
/// Configuration keys (all under the <c>Email</c> root):
/// <list type="bullet">
///   <item><description><c>Email:Smtp:Host</c> — SMTP server hostname
///     (required at <b>sender</b> start-up, not here).</description></item>
///   <item><description><c>Email:From</c> — Default "from" address when
///     <see cref="EmailMessage.From"/> is null.</description></item>
///   <item><description><c>Email:OutboxMaxAttempts</c> — delivery-attempt
///     ceiling written onto the row at enqueue time. Default 5.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class SmtpEmailService : IEmailService
{
    private readonly IEmailOutboxRepository _outbox;
    private readonly IEventRepository _events;
    private readonly ITenantContext _tenantContext;
    private readonly IConfiguration _config;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(
        IEmailOutboxRepository outbox,
        IEventRepository events,
        ITenantContext tenantContext,
        IConfiguration config,
        ILogger<SmtpEmailService> logger)
    {
        _outbox = outbox;
        _events = events;
        _tenantContext = tenantContext;
        _config = config;
        _logger = logger;
    }

    public async Task<Guid> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var fromAddress = message.From
            ?? _config["Email:From"]
            ?? throw new InvalidOperationException(
                "Either EmailMessage.From or Email:From configuration must be provided.");

        var maxAttempts = _config.GetValue("Email:OutboxMaxAttempts", 5);
        var now = DateTime.UtcNow;
        var tenantId = message.TenantId ?? _tenantContext.TenantId;

        var row = new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = message.UserId,
            Template = message.Template ?? "unknown",
            ToAddress = message.To,
            Subject = message.Subject,
            HtmlBody = message.Html,
            TextBody = message.Text,
            FromAddress = fromAddress,
            Status = "pending",
            Attempts = 0,
            MaxAttempts = maxAttempts,
            NextAttemptAt = now,
        };

        var enqueued = await _outbox.EnqueueAsync(row, ct);

        // Event emission MUST NOT fail the SendAsync contract — callers upstream
        // rely on the Guid return. If the event store is down we log and carry
        // on; the outbox row is the durable record of intent.
        try
        {
            await EmitQueuedAsync(enqueued, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Email queued event emission failed txn={TxnId}", enqueued.Id);
        }

        _logger.LogInformation(
            "Email queued to outbox txn={TxnId} template={Template}",
            enqueued.Id, enqueued.Template);

        return enqueued.Id;
    }

    private async Task EmitQueuedAsync(EmailOutboxMessage row, CancellationToken ct)
    {
        _ = ct; // IEventRepository doesn't take CT today; keep signature future-proof.

        // CodeQL-safe: no recipient / subject / body anywhere in tags or data.
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
        };

        await _events.AppendAsync(new DomainEvent
        {
            Type = EmailEventTypes.Queued,
            TenantId = row.TenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        });
    }
}
