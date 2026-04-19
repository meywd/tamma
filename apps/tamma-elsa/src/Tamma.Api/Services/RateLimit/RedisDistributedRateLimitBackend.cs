using StackExchange.Redis;

namespace Tamma.Api.Services.RateLimit;

/// <summary>
/// Redis/Valkey-backed <see cref="IDistributedRateLimitBackend"/>. Uses a
/// single atomic Lua script for increment+TTL so pods never race each
/// other. Audit finding auth/014 follow-up.
///
/// <para>
/// Window semantics: fixed-window. Each key carries an integer counter
/// and a TTL. The first <see cref="Increment"/> inside a window sets the
/// TTL via <c>SET EX</c>; subsequent increments in the same window use
/// <c>INCR</c> without touching the TTL. When the TTL pops, the whole
/// window resets. This is weaker than the in-memory sliding window, but
/// for 3 req/hour quotas the difference is imperceptible and the single
/// round-trip per call stays cheap.
/// </para>
/// </summary>
public sealed class RedisDistributedRateLimitBackend : IDistributedRateLimitBackend
{
    // Lua: if the key doesn't exist, set it to 1 with an EX; else INCR.
    // Returning the final count keeps the call a single round-trip.
    private const string IncrementScript = @"
        local current = redis.call('INCR', KEYS[1])
        if current == 1 then
            redis.call('EXPIRE', KEYS[1], ARGV[1])
        end
        return current";

    private readonly IConnectionMultiplexer _multiplexer;

    public RedisDistributedRateLimitBackend(IConnectionMultiplexer multiplexer)
    {
        _multiplexer = multiplexer;
    }

    public long Increment(string compositeKey, TimeSpan ttl)
    {
        var db = _multiplexer.GetDatabase();
        var ttlSeconds = Math.Max(1, (long)Math.Ceiling(ttl.TotalSeconds));
        var result = db.ScriptEvaluate(
            IncrementScript,
            new RedisKey[] { compositeKey },
            new RedisValue[] { ttlSeconds });
        return (long)result;
    }

    public long Count(string compositeKey, TimeSpan ttl)
    {
        var db = _multiplexer.GetDatabase();
        var value = db.StringGet(compositeKey);
        if (value.IsNullOrEmpty) return 0;
        return (long)value;
    }
}
