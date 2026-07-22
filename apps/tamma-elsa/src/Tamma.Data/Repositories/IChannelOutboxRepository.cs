using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 39-18 (D4/D6) — persistence port for the per-tenant channel
/// store-and-forward outbox. Pure CRUD; delivery/scheduling policy lives in
/// <c>ChannelOutboxService</c> (write-time push) and <c>ChannelOutboxSweeper</c>
/// (re-publish of stale rows). Mirrors <see cref="IEmailOutboxRepository"/>'s
/// strictly-tenant-scoped shape: every operation requires a <c>tenantId</c> so the
/// implementation routes to the correct per-tenant DB.
///
/// <para>Ack is idempotent (acking an acked row is a no-op returning <c>false</c>),
/// so a duplicate hub delivery can never double-process — the transport is never the
/// source of truth (AC6).</para>
/// </summary>
public interface IChannelOutboxRepository
{
    /// <summary>
    /// Insert a new message in <c>pending</c> state into the tenant's outbox and
    /// return the persisted row. <see cref="ChannelOutboxMessage.Id"/> is the
    /// <c>ChannelEnvelope</c> message id (UUID v7) and MUST be set by the caller.
    /// </summary>
    Task<ChannelOutboxMessage> EnqueueAsync(ChannelOutboxMessage msg, CancellationToken ct = default);

    /// <summary>
    /// List a consumer's unacked rows (<c>pending</c> or <c>delivered</c>) ordered by
    /// <see cref="ChannelOutboxMessage.Id"/> (UUID v7 = time order) — the connect-time
    /// replay set. Scoped to (<paramref name="tenantId"/>, <paramref name="audience"/>,
    /// <paramref name="recipientUserId"/>): an orchestrator consumer passes
    /// <c>recipientUserId = null</c>; a user consumer passes its own id, so user A's
    /// list never returns user B's rows.
    /// </summary>
    Task<List<ChannelOutboxMessage>> ListUnackedAsync(
        Guid tenantId, string audience, Guid? recipientUserId, int limit, CancellationToken ct = default);

    /// <summary>
    /// Mark a row <c>delivered</c> (stamping <see cref="ChannelOutboxMessage.DeliveredAt"/>
    /// and bumping <see cref="ChannelOutboxMessage.Attempts"/>). No-op if the row is
    /// already <c>acked</c>.
    /// </summary>
    Task MarkDeliveredAsync(Guid tenantId, Guid messageId, CancellationToken ct = default);

    /// <summary>
    /// Idempotently ack a row: flips it to <c>acked</c> and stamps
    /// <see cref="ChannelOutboxMessage.AckedAt"/>. Returns <c>true</c> when THIS call
    /// transitioned the row, <c>false</c> when the row was missing, already acked, or
    /// not owned by <paramref name="recipientUserId"/> (per-recipient ack).
    /// </summary>
    Task<bool> AckAsync(Guid tenantId, Guid messageId, Guid? recipientUserId, CancellationToken ct = default);

    /// <summary>
    /// List stale unacked rows for the sweeper: <c>pending</c> rows (never delivered —
    /// crash between persist and publish) and <c>delivered</c> rows whose
    /// <see cref="ChannelOutboxMessage.DeliveredAt"/> is older than
    /// <paramref name="staleBefore"/> (missed reconnect race). Ordered by
    /// <see cref="ChannelOutboxMessage.Id"/>.
    /// </summary>
    Task<List<ChannelOutboxMessage>> ListStaleAsync(
        Guid tenantId, DateTime staleBefore, int limit, CancellationToken ct = default);

    /// <summary>
    /// Cross-tenant drain support — the active tenant ids that have at least one
    /// unacked (<c>pending</c>/<c>delivered</c>) row, so the sweeper can walk tenants
    /// without a static enumeration (mirrors the email repo's drain-pass precedent).
    /// </summary>
    Task<IReadOnlyList<Guid>> ListTenantsWithPendingAsync(CancellationToken ct = default);
}
