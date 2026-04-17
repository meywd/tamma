using Tamma.Data.Entities;

namespace Tamma.Api.Services.Email;

/// <summary>
/// Thin seam around the SMTP client so <c>OutboxSmtpSender</c> can be tested
/// without spinning up a real MailKit client. Production uses
/// <see cref="MailKitSmtpTransport"/>; tests supply a mock.
/// </summary>
public interface ISmtpTransport
{
    /// <summary>
    /// Deliver <paramref name="message"/> via the underlying SMTP relay. Throws
    /// for ANY transport failure (connect, auth, send). The sender catches,
    /// logs (txn id only), and applies the retry / failure policy.
    /// </summary>
    Task SendAsync(EmailOutboxMessage message, CancellationToken ct);
}
