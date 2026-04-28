using System.Text.Json;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Email;

/// <summary>
/// Outbox-backed <see cref="IEmailService"/>. <see cref="SendAsync"/> does not
/// talk to SMTP — it persists the message in the per-tenant
/// <c>email_outbox</c> (when <see cref="EmailMessage.TenantId"/> is set)
/// or the platform <c>platform_email_outbox</c> (when no tenant is
/// supplied) and emits a <see cref="EmailEventTypes.Queued"/> event. The
/// actual SMTP delivery is performed asynchronously by
/// <c>OutboxSmtpSender</c>, which also emits
/// <see cref="EmailEventTypes.Sent"/> / <see cref="EmailEventTypes.Failed"/>.
///
/// <para>Story 28-1 PR B — the platform vs tenant routing happens here.
/// Callers shouldn't need to know which repo to use; they set
/// <see cref="EmailMessage.TenantId"/> when the email belongs to a real
/// tenant org (invite, internal notification) and leave it unset for
/// platform-scope emails (verification, password reset, welcome) that
/// fire before/after a tenant DB exists. The decision matrix is in
/// <c>.dev/decisions/story-28-1-design-calls.md</c> §5 plus the
/// commit body of PR B itself.</para>
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
    private readonly IPlatformEmailOutboxRepository _platformOutbox;
    private readonly IEventRepository _events;
    private readonly ITenantContext _tenantContext;
    private readonly IConfiguration _config;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(
        IEmailOutboxRepository outbox,
        IPlatformEmailOutboxRepository platformOutbox,
        IEventRepository events,
        ITenantContext tenantContext,
        IConfiguration config,
        ILogger<SmtpEmailService> logger)
    {
        _outbox = outbox;
        _platformOutbox = platformOutbox;
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

        // Story 28-1 PR B: routing.
        //
        // 1. message.TenantId is the most authoritative signal — callers
        //    that intentionally set null mean "platform scope" (no tenant DB
        //    exists yet, e.g. registration verification email).
        // 2. When the message's TenantId is null but the ambient
        //    ITenantContext.TenantId is set, prefer the ambient one. This
        //    preserves the historical "implicit tenant" behaviour for
        //    callers that haven't been audited yet — they'll keep working
        //    after PR D's physical move.
        // 3. Both null → platform path.
        var tenantId = message.TenantId ?? _tenantContext.TenantId;

        var enqueuedId = tenantId is Guid tid && tid != Guid.Empty
            ? await EnqueueTenantAsync(message, fromAddress, maxAttempts, now, tid, ct)
            : await EnqueuePlatformAsync(message, fromAddress, maxAttempts, now, ct);

        // Event emission MUST NOT fail the SendAsync contract — callers upstream
        // rely on the Guid return. If the event store is down we log and carry
        // on; the outbox row is the durable record of intent.
        try
        {
            await EmitQueuedAsync(enqueuedId, message.Template ?? "unknown", tenantId, message.UserId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Email queued event emission failed txn={TxnId}", enqueuedId);
        }

        _logger.LogInformation(
            "Email queued to outbox txn={TxnId} template={Template} scope={Scope}",
            enqueuedId, message.Template ?? "unknown",
            tenantId is null ? "platform" : "tenant");

        return enqueuedId;
    }

    private async Task<Guid> EnqueueTenantAsync(
        EmailMessage message, string fromAddress, int maxAttempts,
        DateTime now, Guid tenantId, CancellationToken ct)
    {
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
        return enqueued.Id;
    }

    private async Task<Guid> EnqueuePlatformAsync(
        EmailMessage message, string fromAddress, int maxAttempts,
        DateTime now, CancellationToken ct)
    {
        var row = new PlatformEmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            TenantId = null,
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
        var enqueued = await _platformOutbox.EnqueueAsync(row, ct);
        return enqueued.Id;
    }

    private async Task EmitQueuedAsync(
        Guid txnId, string template, Guid? tenantId, Guid? userId, CancellationToken ct)
    {
        _ = ct; // IEventRepository doesn't take CT today; keep signature future-proof.

        // CodeQL-safe: no recipient / subject / body anywhere in tags or data.
        var tags = new Dictionary<string, string?>
        {
            ["txn_id"] = txnId.ToString(),
            ["template"] = template,
            ["tenant_id"] = tenantId?.ToString(),
            ["user_id"] = userId?.ToString(),
        };

        var data = new Dictionary<string, object?>
        {
            ["provider"] = "smtp",
        };

        await _events.AppendAsync(new DomainEvent
        {
            Type = EmailEventTypes.Queued,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        });
    }
}
