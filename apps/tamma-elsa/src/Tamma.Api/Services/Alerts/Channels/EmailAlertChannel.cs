using System.Text.Json;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Alerts.Channels;

/// <summary>
/// Story 1.5-37 (Wave C.1) — email delivery channel. Enqueues a row
/// on the control-plane <c>platform_email_outbox</c> (Story 28-6);
/// the existing <c>OutboxSmtpSender</c> background service handles
/// the SMTP send + retry.
///
/// <para><b>Config schema</b>:
/// <c>{"toAddress":"ops@...","subjectPrefix":"[ALERT] "}</c>.
/// <c>toAddress</c> is required; <c>subjectPrefix</c> defaults to
/// <c>"[Tamma Alert]"</c>. No credentials are consumed at this layer
/// — the SMTP transport credentials live in the Story 29-9 cabinet
/// and are resolved by <c>MailKitSmtpTransport</c>.</para>
/// </summary>
public sealed class EmailAlertChannel : IAlertChannel
{
    public string ChannelType => AlertChannelType.Email;

    private readonly ControlPlaneDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IConfiguration _configuration;

    public EmailAlertChannel(
        ControlPlaneDbContext db,
        TimeProvider timeProvider,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(configuration);
        _db = db;
        _timeProvider = timeProvider;
        _configuration = configuration;
    }

    public async Task<DeliveryResult> SendAsync(
        Alert alert, AlertChannel channel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(channel);

        string? toAddress;
        string subjectPrefix;
        try
        {
            var config = ParseConfig(channel.Config);
            toAddress = config.ToAddress;
            subjectPrefix = config.SubjectPrefix;
        }
        catch (Exception ex)
        {
            return new DeliveryResult(
                Success: false,
                Error: $"Channel config parse failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(toAddress))
        {
            return new DeliveryResult(
                Success: false,
                Error: "Channel config missing 'toAddress'.");
        }

        var fromAddress = _configuration["Email:FromAddress"]
            ?? _configuration["Smtp:From"]
            ?? "alerts@tamma.dev";
        var subject = $"{subjectPrefix}{alert.Severity.ToUpperInvariant()}: {alert.Title}";
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var text = BuildPlainTextBody(alert);
        var html = BuildHtmlBody(alert);

        try
        {
            _db.PlatformEmailOutbox.Add(new PlatformEmailOutboxMessage
            {
                TenantId = alert.TenantId,
                UserId = null,
                Template = "alert.notification",
                ToAddress = toAddress,
                Subject = Truncate(subject, 512),
                HtmlBody = html,
                TextBody = text,
                FromAddress = fromAddress,
                Status = "pending",
                Attempts = 0,
                MaxAttempts = 5,
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new DeliveryResult(Success: true, Error: null);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(
                Success: false,
                Error: $"Outbox enqueue failed: {ex.Message}");
        }
    }

    private static EmailChannelConfig ParseConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return new EmailChannelConfig(null, "[Tamma Alert] ");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var to = root.TryGetProperty("toAddress", out var toEl)
            ? toEl.GetString()
            : null;
        var prefix = root.TryGetProperty("subjectPrefix", out var prefixEl)
            ? prefixEl.GetString() ?? "[Tamma Alert] "
            : "[Tamma Alert] ";
        return new EmailChannelConfig(to, prefix);
    }

    private static string BuildPlainTextBody(Alert alert) =>
        $"""
        [{alert.Severity.ToUpperInvariant()}] {alert.Title}

        {alert.Description}

        Alert ID: {alert.Id}
        Correlation: {alert.CorrelationId ?? "(none)"}
        Raised at (UTC): {alert.CreatedAt:O}
        """;

    private static string BuildHtmlBody(Alert alert)
    {
        var color = alert.Severity switch
        {
            AlertSeverity.Critical => "#c0392b",
            AlertSeverity.Warning => "#d35400",
            _ => "#2980b9",
        };
        var safeTitle = System.Net.WebUtility.HtmlEncode(alert.Title);
        var safeDesc = System.Net.WebUtility.HtmlEncode(alert.Description)
            .Replace("\n", "<br/>");
        var safeCorr = System.Net.WebUtility.HtmlEncode(
            alert.CorrelationId ?? "(none)");
        return $"""
        <!doctype html>
        <html><body style="font-family:-apple-system,BlinkMacSystemFont,sans-serif;">
          <div style="border-left:4px solid {color};padding:12px 16px;">
            <h2 style="margin:0;color:{color};">{alert.Severity.ToUpperInvariant()}: {safeTitle}</h2>
            <p>{safeDesc}</p>
            <p style="font-size:12px;color:#888;">
              Alert ID: {alert.Id}<br/>
              Correlation: {safeCorr}<br/>
              Raised at (UTC): {alert.CreatedAt:O}
            </p>
          </div>
        </body></html>
        """;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];

    private sealed record EmailChannelConfig(string? ToAddress, string SubjectPrefix);
}
