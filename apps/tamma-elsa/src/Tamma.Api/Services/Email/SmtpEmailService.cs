using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Tamma.Api.Logging;

namespace Tamma.Api.Services.Email;

/// <summary>
/// MailKit-backed <see cref="IEmailService"/> that delivers messages through
/// a configured SMTP relay. Configuration keys (all under the <c>Email</c>
/// root):
/// <list type="bullet">
///   <item><description><c>Email:Smtp:Host</c> — SMTP server hostname (required).</description></item>
///   <item><description><c>Email:Smtp:Port</c> — Server port (default 587).</description></item>
///   <item><description><c>Email:Smtp:Username</c> — Auth username (optional; anonymous if blank).</description></item>
///   <item><description><c>Email:Smtp:Password</c> — Auth password (optional).</description></item>
///   <item><description><c>Email:Smtp:UseSsl</c> — <c>true</c> → SslOnConnect; <c>false</c> → StartTlsWhenAvailable.</description></item>
///   <item><description><c>Email:From</c> — Default "from" address when <see cref="EmailMessage.From"/> is null.</description></item>
/// </list>
/// </summary>
public sealed class SmtpEmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IConfiguration config, ILogger<SmtpEmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var host = _config["Email:Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException(
                "Email:Smtp:Host must be configured before using SmtpEmailService.");

        var port = _config.GetValue("Email:Smtp:Port", 587);
        var username = _config["Email:Smtp:Username"];
        var password = _config["Email:Smtp:Password"];
        var useSsl = _config.GetValue("Email:Smtp:UseSsl", false);
        var fromAddress = message.From
            ?? _config["Email:From"]
            ?? throw new InvalidOperationException(
                "Either EmailMessage.From or Email:From configuration must be provided.");

        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(fromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder
        {
            HtmlBody = message.Html,
            TextBody = message.Text,
        }.ToMessageBody();

        var secureOptions = useSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(host, port, secureOptions, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(username))
                await client.AuthenticateAsync(username, password, ct).ConfigureAwait(false);

            await client.SendAsync(mime, ct).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);

            // DO NOT log full body, recipient address, or subject — they may
            // contain one-time tokens or PII that land in log aggregators.
            // Surface only the recipient DOMAIN and the SMTP host so ops can
            // see "N failures to outlook.com" without leaking identities.
            _logger.LogInformation(
                "Email delivered via SMTP: domain={Domain} host={Host}",
                RecipientDomain(message.To), host);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SMTP send failed: domain={Domain} host={Host}",
                RecipientDomain(message.To), host);
            throw;
        }
    }

    /// <summary>
    /// Returns the domain portion of an email address (e.g. "example.com"),
    /// or "&lt;invalid&gt;" when no '@' is present. The local part is dropped so
    /// operational logs do not carry PII. CRLF/control chars are still stripped
    /// defensively via <see cref="LogSanitizer"/>.
    /// </summary>
    private static string RecipientDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "<empty>";
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1) return "<invalid>";
        return LogSanitizer.Clean(email[(at + 1)..]);
    }
}
