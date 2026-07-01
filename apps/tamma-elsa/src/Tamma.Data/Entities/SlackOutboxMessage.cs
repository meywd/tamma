namespace Tamma.Data.Entities;

/// <summary>
/// Story 38-3 (Epic 38, Class D) — control-plane Slack notification outbox.
/// The fire-and-forget analogue of <see cref="PlatformEmailOutboxMessage"/>:
/// the engine's <c>SlackActivity</c> no longer holds the Slack credential;
/// it posts the (already-formatted) notification intent to
/// <c>POST /api/v1/notifications/slack</c>, which writes one <c>pending</c>
/// row here. The out-of-band <c>OutboxSlackSender</c> — the ONLY holder of the
/// Slack webhook credential — drains this table and performs the transport.
///
/// <para>Control-plane / public-schema table (it must deliver regardless of
/// tenant-DB routing, same rationale as <c>platform_email_outbox</c>). Owner
/// scope is <c>TenantId</c> XOR <c>UserId</c> (SaaS → <c>TenantId</c> from
/// <c>X-Tenant-Id</c>; single-user → <c>UserId</c>), exactly like the email
/// outbox. Terminal outcomes are audited to <c>platform_events</c>; the row,
/// its <see cref="LastError"/>, and every event payload are token-free by
/// contract (the webhook URL/secret is never written here).</para>
/// </summary>
public sealed class SlackOutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>SaaS-mode owner scope. XOR with <see cref="UserId"/>.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Single-user-mode owner scope. XOR with <see cref="TenantId"/>.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Target channel for a channel post (null for a DM-only intent).</summary>
    public string? Channel { get; set; }

    /// <summary>Target Slack user id for a DM (null for a channel-only intent). NOT a Tamma UserId.</summary>
    public string? TargetUserId { get; set; }

    /// <summary>Message type carried for audit (Info|Warning|Success|Error|Celebration). Formatting is applied engine-side.</summary>
    public string MessageType { get; set; } = "Info";

    /// <summary>The already-formatted message body (emoji/assessment/guidance templating done engine-side; token-free).</summary>
    public string Body { get; set; } = null!;

    /// <summary>pending | sending | sent | failed.</summary>
    public string Status { get; set; } = "pending";

    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTime NextAttemptAt { get; set; }

    /// <summary>Last transport error — key-free (webhook URL/secret redacted before write).</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}
