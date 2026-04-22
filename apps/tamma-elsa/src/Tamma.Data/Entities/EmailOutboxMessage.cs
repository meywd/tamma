namespace Tamma.Data.Entities;

/// <summary>
/// A single deliverable email persisted in the store-and-forward outbox.
///
/// <para>
/// The row doubles as a correlation record: <see cref="Id"/> is the transaction
/// id every log line and domain event references. Recipient, subject, and body
/// are stored here but are <b>never</b> emitted to logs or to the domain-event
/// stream — CodeQL's private-data taint model treats any substring of these
/// values as tainted.
/// </para>
///
/// <para>Status transitions:</para>
/// <list type="bullet">
///   <item><description><c>pending</c> → <c>sending</c> — claimed by the sender.</description></item>
///   <item><description><c>sending</c> → <c>sent</c> — SMTP/HTTP transport succeeded.</description></item>
///   <item><description><c>sending</c> → <c>pending</c> — transient failure; retry after
///     <see cref="NextAttemptAt"/>. <see cref="Attempts"/> is incremented.</description></item>
///   <item><description><c>sending</c> → <c>failed</c> — final attempt failed
///     (<see cref="Attempts"/> &gt;= <see cref="MaxAttempts"/>).</description></item>
/// </list>
/// </summary>
public class EmailOutboxMessage
{
    /// <summary>
    /// Primary key AND the transaction id for this delivery attempt. The same
    /// value appears in log lines ("txn={TxnId}") and domain-event tags.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant; <c>null</c> for system-scope mail (admin invites, etc).</summary>
    public Guid? TenantId { get; set; }

    /// <summary>User the mail pertains to. May be null when the user record does not yet exist.</summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// Template key used to compose the body (e.g. <c>verification</c>,
    /// <c>password-reset</c>, <c>welcome</c>). Free-form — no FK.
    /// </summary>
    public string Template { get; set; } = null!;

    /// <summary>RFC-5322 recipient. Stored for delivery; never logged.</summary>
    public string ToAddress { get; set; } = null!;

    /// <summary>Subject line. Stored for delivery; never logged.</summary>
    public string Subject { get; set; } = null!;

    /// <summary>HTML body.</summary>
    public string HtmlBody { get; set; } = null!;

    /// <summary>Plain-text body.</summary>
    public string TextBody { get; set; } = null!;

    /// <summary>From address. Typically the configured <c>Email:From</c>.</summary>
    public string FromAddress { get; set; } = null!;

    /// <summary>One of <c>pending | sending | sent | failed</c>.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Number of attempts consumed so far. 0 on insert.</summary>
    public int Attempts { get; set; }

    /// <summary>Attempt ceiling; row flips to <c>failed</c> on attempt <see cref="MaxAttempts"/>.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Earliest time the sender is allowed to claim this row. Set on each
    /// requeue via exponential backoff.
    /// </summary>
    public DateTime NextAttemptAt { get; set; }

    /// <summary>Message from the last transport error. Cleared on success.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Set when <see cref="Status"/> transitions to <c>sent</c>.</summary>
    public DateTime? SentAt { get; set; }
}
