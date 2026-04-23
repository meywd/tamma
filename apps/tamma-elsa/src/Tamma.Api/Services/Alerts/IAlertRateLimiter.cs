namespace Tamma.Api.Services.Alerts;

/// <summary>
/// Story 5.6 (Wave C.1) — per-rule token bucket keyed by
/// <c>RuleId</c>. When a rule exceeds its rate ceiling the
/// <see cref="IAlertSink"/> writes a
/// <see cref="AlertDeliveryStatus.DroppedRateLimit"/> delivery-attempt
/// row for audit and short-circuits.
///
/// <para>Configuration: defaults to <b>5 alerts per minute per rule</b>
/// per the story spec. A <c>null</c> <c>ruleId</c> — alerts raised
/// directly through <c>IAlertSink</c> without going through the Wave
/// C.2 rule engine — bypasses rate limiting entirely (there's no
/// bucket key).</para>
/// </summary>
public interface IAlertRateLimiter
{
    /// <summary>
    /// Check the bucket for <paramref name="ruleId"/> and consume a
    /// token if available. Returns <c>true</c> when the caller may
    /// proceed with the raise; <c>false</c> when the bucket is empty
    /// and the alert must be dropped + audited.
    ///
    /// <para><c>null</c> <paramref name="ruleId"/> always returns
    /// <c>true</c> — rate limiting is rule-keyed, and alerts without
    /// a rule bypass the limiter.</para>
    /// </summary>
    bool TryConsume(Guid? ruleId);
}
