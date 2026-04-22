namespace Tamma.Data.Entities;

/// <summary>
/// Control-plane email outbox — same shape as
/// <see cref="EmailOutboxMessage"/> but for system-scope mail that must
/// deliver before a tenant DB exists (registration verification) or after
/// one is gone (deletion confirmation).
///
/// <para>Doc 01 §1.2 row 26 + Doc 03 §7.1 conflict resolution: welcome
/// mail, registration verification, password reset, platform admin
/// notifications all queue here. Tenant-scoped mail (workflow alerts,
/// invites *into* a live tenant) stays on the per-tenant
/// <c>email_outbox</c>.</para>
///
/// <para>The existing <c>OutboxSmtpSender</c> single-table scan is reused
/// against this table by Story 28-6 — only the table name changes.</para>
/// </summary>
public class PlatformEmailOutboxMessage
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public string Template { get; set; } = null!;
    public string ToAddress { get; set; } = null!;
    public string Subject { get; set; } = null!;
    public string HtmlBody { get; set; } = null!;
    public string TextBody { get; set; } = null!;
    public string FromAddress { get; set; } = null!;
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTime NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}
