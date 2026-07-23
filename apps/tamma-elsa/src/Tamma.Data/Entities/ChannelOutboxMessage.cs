namespace Tamma.Data.Entities;

/// <summary>
/// Story 39-18 (Design Decision D4) — a single channel message persisted in the
/// per-tenant store-and-forward <c>channel_outbox</c>. Copied in shape from
/// <see cref="EmailOutboxMessage"/> (status transitions, lease semantics) minus the
/// SMTP fields.
///
/// <para>The outbox is the SOURCE OF TRUTH the transport is not (AC6): every
/// request/escalation/guidance/task message is persisted here BEFORE any hub send,
/// a disconnected consumer receives its unacked rows on reconnect by replay, and ack
/// is idempotent — so a duplicate hub delivery can never double-process.</para>
///
/// <para>Status transitions:</para>
/// <list type="bullet">
///   <item><description><c>pending</c> — persisted, not yet delivered to any live
///     connection (the degraded case: a hub send to zero connections is a silent
///     no-op, so the row simply waits).</description></item>
///   <item><description><c>pending</c> → <c>delivered</c> — pushed to a live
///     consumer (write-time push or connect-time replay).</description></item>
///   <item><description><c>delivered</c> → <c>acked</c> — the consumer acked
///     (idempotent; acking an acked row is a no-op).</description></item>
/// </list>
/// One row per RECIPIENT: user-audience messages fan out at WRITE time to one row per
/// 39-20-eligible recipient, so ack is per-user.
/// </summary>
public class ChannelOutboxMessage
{
    /// <summary>
    /// Primary key AND the <see cref="Tamma.Core.Documents.Channels.ChannelEnvelope"/>
    /// message id — a UUID v7, so ordering by <see cref="Id"/> is time-ordered replay
    /// without a separate sequence column.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant (the channel is per-tenant). Required.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The <c>ChannelAudience</c> wire string (<c>orchestrator</c> | <c>user</c>).</summary>
    public string Audience { get; set; } = null!;

    /// <summary>
    /// The recipient user. <c>null</c> = the tenant's orchestrator agent
    /// (orchestrator-audience rows). Set to a specific user for a fanned-out
    /// user-audience row so ack is per-user.
    /// </summary>
    public Guid? RecipientUserId { get; set; }

    /// <summary>The <c>ChannelMessage</c> discriminator kind (e.g. <c>acceptance-request</c>).</summary>
    public string Kind { get; set; } = null!;

    /// <summary>
    /// The full <c>ChannelEnvelope</c> serialized via <c>DocumentJson.Options</c>
    /// (jsonb). Stored whole so replay is a clean deserialize — the transport just
    /// re-sends it. The denormalized columns above make the row queryable without
    /// parsing the payload.
    /// </summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>
    /// The 39-8 decision-session id this row correlates to (when the payload carries
    /// one — acceptance requests / task assignments). Denormalized for correlation.
    /// </summary>
    public Guid? DecisionSessionId { get; set; }

    /// <summary>One of <c>pending | delivered | acked</c>.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Number of delivery attempts (bumped on each publish). 0 on insert.</summary>
    public int Attempts { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Set when <see cref="Status"/> transitions to <c>delivered</c>.</summary>
    public DateTime? DeliveredAt { get; set; }

    /// <summary>Set when <see cref="Status"/> transitions to <c>acked</c>.</summary>
    public DateTime? AckedAt { get; set; }
}
