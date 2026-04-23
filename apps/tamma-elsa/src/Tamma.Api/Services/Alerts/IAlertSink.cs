namespace Tamma.Api.Services.Alerts;

/// <summary>
/// Story 5.6 (Wave C.1) — the single write-side API the rest of the
/// code uses to raise an alert. Every call:
/// <list type="number">
///   <item><description>Runs the rate limiter (per <c>RuleId</c>); if
///     saturated, records a <c>dropped_rate_limit</c> delivery-attempt
///     row for audit and returns the dropped alert's id (no alert row
///     written, no channel fan-out).</description></item>
///   <item><description>Writes the <c>alerts</c> row.</description></item>
///   <item><description>Writes a <c>pending</c> <c>alert_delivery_attempts</c>
///     row for every matching enabled channel (platform-scoped channels
///     if <c>TenantId</c> is null; tenant-scoped channels if it's set).</description></item>
///   <item><description>Emits an <c>ALERT.RAISED</c> DCB event.</description></item>
/// </list>
///
/// <para>The <see cref="NotificationDispatcher"/> background service
/// drains the <c>alert_delivery_attempts</c> rows out-of-band — raise is
/// synchronous only up to the DB commit, so hot paths aren't slowed by
/// network-bound channel invocations.</para>
/// </summary>
public interface IAlertSink
{
    /// <summary>
    /// Raise an alert. Returns the id of the <c>alerts</c> row written
    /// (or, if the rate limiter dropped the alert, <c>Guid.Empty</c> so
    /// callers can distinguish delivered from dropped without a second
    /// read).
    /// </summary>
    /// <param name="payload">Alert input.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Payload severity / title /
    /// description are invalid.</exception>
    Task<AlertRaiseResult> RaiseAsync(AlertPayload payload, CancellationToken ct = default);
}

/// <summary>
/// Result of <see cref="IAlertSink.RaiseAsync"/>. Distinguishes
/// delivered from dropped alerts so metrics / tests can assert on the
/// rate-limit behaviour without a second DB read.
/// </summary>
public sealed record AlertRaiseResult(
    Guid AlertId,
    bool Delivered,
    int MatchedChannels,
    bool DroppedByRateLimit);
