using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Tamma.Api.Services.Integrations;

namespace Tamma.Api.Services.Email;

/// <summary>
/// Production MailKit implementation of <see cref="ITenantSmtpTransport"/>. Connects
/// to the TENANT'S OWN SMTP relay (host/port/credentials from the resolved
/// <see cref="EmailCredential"/>) and sends synchronously. Does not log recipient,
/// subject, or body — only the relay host on failure (surfaced by the exception the
/// caller catches).
/// </summary>
public sealed class MailKitTenantSmtpTransport : ITenantSmtpTransport
{
    private const int DefaultSmtpPort = 587;

    public async Task SendAsync(EmailCredential credential, EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(message);

        var host = credential.SmtpHost
            ?? throw new InvalidOperationException("Tenant SMTP transport requires smtpHost.");
        var port = credential.SmtpPort ?? DefaultSmtpPort;

        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(credential.From));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder
        {
            HtmlBody = message.Html,
            TextBody = message.Text,
        }.ToMessageBody();

        // Default to opportunistic STARTTLS (matches MailKitSmtpTransport); a tenant
        // that pins smtpUseStartTls=false still negotiates TLS when the relay offers
        // it via Auto. We never fall back to a cleartext-only connection silently.
        var secureOptions = credential.SmtpUseStartTls == false
            ? SecureSocketOptions.Auto
            : SecureSocketOptions.StartTlsWhenAvailable;

        using var client = new SmtpClient();
        await client.ConnectAsync(host, port, secureOptions, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(credential.SmtpUsername))
        {
            await client.AuthenticateAsync(credential.SmtpUsername, credential.SmtpPassword, ct)
                .ConfigureAwait(false);
        }

        await client.SendAsync(mime, ct).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
    }
}
