namespace Tamma.Api.Services.Email;

/// <summary>
/// Canonical event-type strings emitted by the email subsystem. They follow
/// the project-wide <c>AGGREGATE.ACTION.STATUS</c> convention and are used by:
/// <list type="bullet">
///   <item><description><see cref="IEmailService"/> implementations — emit
///     <see cref="Queued"/> when a message is accepted for delivery.</description></item>
///   <item><description><c>OutboxSmtpSender</c> + <c>ResendEmailService</c>
///     — emit <see cref="Sent"/> on successful transport, or
///     <see cref="Failed"/> when the delivery is permanently abandoned.</description></item>
/// </list>
///
/// <para>
/// Tags on each event carry the transaction id (<c>txn_id</c>), template key,
/// tenant id, and user id. Event <c>data</c> may carry non-sensitive metadata
/// (provider name, http status, error_class). <b>Recipient, subject, and body
/// are never placed on events</b> — the outbox table is the only place that
/// persists them.
/// </para>
/// </summary>
public static class EmailEventTypes
{
    /// <summary>Emitted when <see cref="IEmailService.SendAsync"/> accepts a message.</summary>
    public const string Queued = "EMAIL.QUEUED.SUCCESS";

    /// <summary>Emitted on successful transport (SMTP 250 / HTTP 2xx).</summary>
    public const string Sent = "EMAIL.SENT.SUCCESS";

    /// <summary>
    /// Emitted when delivery is permanently abandoned. Either the HTTP provider
    /// returned a non-retryable status, or the SMTP sender exhausted every
    /// retry attempt.
    /// </summary>
    public const string Failed = "EMAIL.SENT.FAILED";
}
