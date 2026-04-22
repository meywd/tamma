namespace Tamma.Api.Services.Email;

/// <summary>
/// Transport-agnostic email delivery abstraction with a transaction-id return
/// value for end-to-end correlation.
///
/// <para>
/// Implementations (<see cref="SmtpEmailService"/>, <c>ResendEmailService</c>,
/// <see cref="InMemoryEmailService"/>) are responsible for emitting an
/// <see cref="EmailEventTypes.Queued"/> event for every accepted message so
/// the event store is the authoritative record of "did we ever try to send
/// this?". Transport success/failure surfaces as a later
/// <see cref="EmailEventTypes.Sent"/> or <see cref="EmailEventTypes.Failed"/>
/// event, emitted by the SMTP sender or the HTTP provider itself.
/// </para>
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Accept <paramref name="message"/> for delivery. Returns the transaction
    /// id (<c>Guid</c>) that correlates the event stream and any log lines the
    /// caller writes. Implementations must <b>not</b> throw for transport
    /// failures — those surface via the domain-event stream — but may still
    /// throw for programmer errors like a null message.
    /// </summary>
    Task<Guid> SendAsync(EmailMessage message, CancellationToken ct = default);
}
