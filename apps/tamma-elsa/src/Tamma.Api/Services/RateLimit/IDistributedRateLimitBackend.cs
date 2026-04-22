namespace Tamma.Api.Services.RateLimit;

/// <summary>
/// Backend abstraction for the sliding-window rate limiter. Implementations
/// share a counter keyed by the composed <c>rate-limit:{scope}|{key}</c>
/// string and expire it after the given TTL. Audit finding auth/014
/// follow-up — multi-pod deployments need a shared backend (Redis/Valkey)
/// so pods don't each see only a fraction of the per-user request volume.
/// </summary>
/// <remarks>
/// <para>
/// This is a <em>fixed-window</em> counter under the hood: each call to
/// <see cref="Increment"/> bumps an integer and (re)sets a TTL; the window
/// ends when the TTL elapses. <see cref="Count"/> returns the current
/// counter value.
/// </para>
/// <para>
/// The sliding-window semantic the tests assert (individual events
/// expiring as the window moves forward) is approximate under this model:
/// the whole counter resets when the TTL pops. That is a deliberate
/// simplification — for 3 req/hour quotas the error is negligible and the
/// Redis round-trips stay cheap.
/// </para>
/// </remarks>
public interface IDistributedRateLimitBackend
{
    /// <summary>
    /// Increment the counter and (re)set the TTL. Returns the new value.
    /// </summary>
    long Increment(string compositeKey, TimeSpan ttl);

    /// <summary>
    /// Return the current counter value (zero if the key has expired or
    /// never existed). The <paramref name="ttl"/> arg is reserved for
    /// backends that need it to interpret "current window"; the in-memory
    /// implementation ignores it.
    /// </summary>
    long Count(string compositeKey, TimeSpan ttl);
}
