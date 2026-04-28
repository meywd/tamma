using System.Collections.Concurrent;

namespace Tamma.Api.Services.Alerts;

/// <summary>
/// Story 5.6 (Wave C.1) — in-memory token-bucket
/// <see cref="IAlertRateLimiter"/> implementation. One bucket per
/// <c>RuleId</c>; unfilled tokens carry over. Thread-safe via a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> and a per-bucket
/// <see cref="SemaphoreSlim"/>.
///
/// <para><b>Ceiling</b>: 5 alerts per minute per rule. Configurable
/// via <see cref="AlertRateLimiterOptions"/> for tests / future rule
/// overrides. The token bucket refills linearly — one token every
/// (<c>60 / Ceiling</c>) seconds — so bursts beyond the ceiling
/// queue at channel fan-out rate rather than being smeared.</para>
///
/// <para><b>In-memory</b>: the bucket state is not persisted, so a
/// process restart resets counters. Acceptable for alert-rate
/// limiting because the dispatcher is the only writer of
/// <c>dropped_rate_limit</c> rows and a restart-time burst is
/// bounded by the same ceiling once buckets re-fill.</para>
/// </summary>
public sealed class TokenBucketAlertRateLimiter : IAlertRateLimiter
{
    private readonly AlertRateLimiterOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, Bucket> _buckets = new();

    public TokenBucketAlertRateLimiter(
        AlertRateLimiterOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (options.CeilingPerMinute <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "CeilingPerMinute must be > 0.");
        _options = options;
        _timeProvider = timeProvider;
    }

    public bool TryConsume(Guid? ruleId)
    {
        // No rule id = no bucket = bypass. This is the Wave C.1
        // default — alerts raised directly via IAlertSink without
        // going through the not-yet-shipped rule engine.
        if (ruleId is null)
            return true;

        var bucket = _buckets.GetOrAdd(
            ruleId.Value,
            _ => new Bucket(_options.CeilingPerMinute));

        var now = _timeProvider.GetUtcNow();
        lock (bucket)
        {
            // Refill: linear refill rate of Ceiling tokens per
            // minute. Capped at Ceiling so a long-idle rule cannot
            // amass more than a minute's worth of capacity.
            var elapsed = now - bucket.LastRefill;
            if (elapsed > TimeSpan.Zero)
            {
                var refill = elapsed.TotalMinutes * _options.CeilingPerMinute;
                bucket.Tokens = Math.Min(
                    _options.CeilingPerMinute,
                    bucket.Tokens + refill);
                bucket.LastRefill = now;
            }

            if (bucket.Tokens >= 1.0)
            {
                bucket.Tokens -= 1.0;
                return true;
            }
            return false;
        }
    }

    private sealed class Bucket
    {
        public double Tokens;
        public DateTimeOffset LastRefill;

        public Bucket(int ceiling)
        {
            Tokens = ceiling;
            // Start from epoch so the first call backfills to
            // the ceiling immediately. The refill math caps at
            // the ceiling so this is safe.
            LastRefill = DateTimeOffset.UnixEpoch;
        }
    }
}

/// <summary>
/// Options for <see cref="TokenBucketAlertRateLimiter"/>.
/// </summary>
public sealed class AlertRateLimiterOptions
{
    /// <summary>
    /// Tokens-per-minute ceiling per rule. Default <b>5</b> per
    /// Story 5.6 §rate-limiting.
    /// </summary>
    public int CeilingPerMinute { get; set; } = 5;
}
