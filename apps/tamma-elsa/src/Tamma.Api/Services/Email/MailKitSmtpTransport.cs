using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Email;

/// <summary>
/// Production MailKit implementation of <see cref="ISmtpTransport"/>. Ported
/// from the old fire-and-forget <see cref="SmtpEmailService"/> implementation.
/// Does not log recipient, subject, or body — only the relay host.
/// </summary>
public sealed class MailKitSmtpTransport : ISmtpTransport
{
    private readonly IConfiguration _config;

    public MailKitSmtpTransport(IConfiguration config)
    {
        _config = config;
    }

    public async Task SendAsync(EmailOutboxMessage message, CancellationToken ct)
    {
        var host = _config["Email:Smtp:Host"]
            ?? throw new InvalidOperationException(
                "Email:Smtp:Host must be configured before starting the SMTP sender.");
        var port = _config.GetValue("Email:Smtp:Port", 587);
        var username = _config["Email:Smtp:Username"];
        var password = _config["Email:Smtp:Password"];
        var useSsl = _config.GetValue("Email:Smtp:UseSsl", false);

        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(message.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.ToAddress));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody,
        }.ToMessageBody();

        var secureOptions = useSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;

        using var client = new SmtpClient();
        await client.ConnectAsync(host, port, secureOptions, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(username))
            await client.AuthenticateAsync(username, password, ct).ConfigureAwait(false);

        await client.SendAsync(mime, ct).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
    }
}
