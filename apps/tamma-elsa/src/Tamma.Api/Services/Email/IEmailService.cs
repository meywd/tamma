namespace Tamma.Api.Services.Email;

/// <summary>
/// Abstraction over transactional email delivery. Register a single
/// implementation per environment:
/// <list type="bullet">
///   <item><description><see cref="SmtpEmailService"/> — production / staging</description></item>
///   <item><description><see cref="InMemoryEmailService"/> — local dev + tests</description></item>
/// </list>
/// The concrete choice lives behind the
/// <c>AddEmailServices</c> composition-root extension so callers depend
/// only on this interface.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Deliver the email. Implementations should be idempotent-safe at the
    /// caller level — callers are responsible for de-duplication if they
    /// retry.
    /// </summary>
    /// <param name="message">The composed message.</param>
    /// <param name="ct">Cancellation for the underlying network call.</param>
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}
