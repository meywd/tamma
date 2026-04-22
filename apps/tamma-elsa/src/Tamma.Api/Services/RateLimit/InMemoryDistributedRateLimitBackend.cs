using System.Collections.Concurrent;

namespace Tamma.Api.Services.RateLimit;

/// <summary>
/// In-process <see cref="IDistributedRateLimitBackend"/>. Default for single
/// pod deployments — matches the behaviour of the pre-Redis port.
///
/// <para>Keeps a sliding-window list of per-key timestamps, prunes
/// entries older than the TTL on every operation, and reports the
/// post-prune count. This gives exact sliding-window semantics for
/// single-pod callers (stronger than the fixed-window approximation the
/// Redis backend uses) at negligible cost for the 3 req/hour quotas
/// currently wired.</para>
/// </summary>
public sealed class InMemoryDistributedRateLimitBackend : IDistributedRateLimitBackend
{
    private readonly ConcurrentDictionary<string, List<DateTime>> _store = new();
    private readonly Func<DateTime> _utcNow;

    public InMemoryDistributedRateLimitBackend() : this(() => DateTime.UtcNow) { }

    public InMemoryDistributedRateLimitBackend(Func<DateTime> utcNow)
    {
        _utcNow = utcNow;
    }

    public long Increment(string compositeKey, TimeSpan ttl)
    {
        var bucket = _store.GetOrAdd(compositeKey, _ => new List<DateTime>());
        lock (bucket)
        {
            Prune(bucket, ttl);
            bucket.Add(_utcNow());
            return bucket.Count;
        }
    }

    public long Count(string compositeKey, TimeSpan ttl)
    {
        if (!_store.TryGetValue(compositeKey, out var bucket)) return 0;
        lock (bucket)
        {
            Prune(bucket, ttl);
            return bucket.Count;
        }
    }

    private void Prune(List<DateTime> bucket, TimeSpan ttl)
    {
        var cutoff = _utcNow() - ttl;
        bucket.RemoveAll(t => t < cutoff);
    }
}
