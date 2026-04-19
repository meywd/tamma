using System.Collections.Concurrent;

namespace Tamma.Api.Services.RateLimit;

/// <summary>
/// Sliding-window per-key rate limiter (in-process). Mirrors the TS
/// <c>resendRateLimit</c> / <c>resetRateLimit</c> Maps in
/// <c>routes/auth/register.ts</c> and <c>routes/auth/password-reset.ts</c>.
///
/// <para>Limit is fixed per scope: 3 events per <see cref="WindowDuration"/>
/// (1 hour). Single-instance only — multi-pod deployments need a Redis/Valkey
/// backend (TODO Story 16-8).</para>
/// </summary>
public interface IRateLimitService
{
    /// <summary>Returns true if the (scope, key) pair is currently over-limit.</summary>
    bool IsLimited(string scope, string key);

    /// <summary>Records a single event against (scope, key). Call after the
    /// successful action so failed branches do not consume quota.</summary>
    void Record(string scope, string key);
}

public class InMemoryRateLimitService : IRateLimitService
{
    public const int MaxEventsPerWindow = 3;
    public static readonly TimeSpan WindowDuration = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, List<DateTime>> _store = new();
    private readonly Func<DateTime> _utcNow;

    public InMemoryRateLimitService() : this(() => DateTime.UtcNow) { }

    // Test seam.
    public InMemoryRateLimitService(Func<DateTime> utcNow)
    {
        _utcNow = utcNow;
    }

    public bool IsLimited(string scope, string key)
    {
        var bucket = _store.GetOrAdd(Compose(scope, key), _ => new List<DateTime>());
        lock (bucket)
        {
            Prune(bucket);
            return bucket.Count >= MaxEventsPerWindow;
        }
    }

    public void Record(string scope, string key)
    {
        var bucket = _store.GetOrAdd(Compose(scope, key), _ => new List<DateTime>());
        lock (bucket)
        {
            Prune(bucket);
            bucket.Add(_utcNow());
        }
    }

    private void Prune(List<DateTime> bucket)
    {
        var cutoff = _utcNow() - WindowDuration;
        bucket.RemoveAll(t => t < cutoff);
    }

    private static string Compose(string scope, string key)
        => $"{scope}|{key.ToLowerInvariant()}";
}
