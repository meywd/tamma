namespace Tamma.Data.Entities;

/// <summary>
/// Story 5.6 / 1.5-37 (Wave C.1) — one row per (<see cref="AlertId"/>,
/// <see cref="ChannelId"/>, <see cref="AttemptNumber"/>) delivery
/// try on the control-plane <c>alert_delivery_attempts</c> table.
/// The <c>NotificationDispatcher</c> background service polls this
/// table for <c>status IN ('pending','failed')</c> rows whose
/// <see cref="NextAttemptAt"/> is in the past, invokes the channel,
/// and records the outcome.
///
/// <para>Retry envelope is fixed at 5 attempts with exponential
/// backoff (<c>30s, 2m, 5m, 15m, 30m</c>; ~52min total window). A
/// <c>dropped_rate_limit</c> status records rate-limiter drops for
/// audit — the attempt never actually goes to the channel.</para>
/// </summary>
public class AlertDeliveryAttempt
{
    public Guid Id { get; set; }
    public Guid AlertId { get; set; }
    public Guid ChannelId { get; set; }

    /// <summary>
    /// 1-indexed attempt counter. Incremented on every failure.
    /// When it crosses 5 the dispatcher gives up and the row stays
    /// in <c>failed</c> state for audit.
    /// </summary>
    public int AttemptNumber { get; set; }

    /// <summary>
    /// One of <c>pending</c>, <c>success</c>, <c>failed</c>,
    /// <c>dropped_rate_limit</c>. Enforced by a CHECK constraint.
    /// </summary>
    public string Status { get; set; } = "pending";

    /// <summary>
    /// Freeform error blurb on failure; <c>null</c> on success.
    /// Truncated to 2KB in the migration via a varchar cap.
    /// </summary>
    public string? Error { get; set; }

    public DateTime? DeliveredAt { get; set; }

    /// <summary>
    /// When the dispatcher should next try this row. Null means
    /// "deliver immediately"; a future timestamp defers the
    /// attempt per the backoff schedule.
    /// </summary>
    public DateTime? NextAttemptAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
