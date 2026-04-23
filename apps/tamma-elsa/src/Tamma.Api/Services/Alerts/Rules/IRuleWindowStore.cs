using System.Collections.Concurrent;

namespace Tamma.Api.Services.Alerts.Rules;

/// <summary>
/// Story 5.6 (Wave C.2) — rolling-window counter backing the
/// <c>count_gte</c> predicate. The store records one timestamp per
/// qualifying event, drops anything older than the window on each
/// call, and returns the current count including the new entry.
///
/// <para>Keyed by <c>(RuleId, GroupKey)</c> so a "3 retry storms per
/// tenant in 5min" rule doesn't let tenant A's storms poison tenant
/// B's bucket.</para>
/// </summary>
public interface IRuleWindowStore
{
    /// <summary>
    /// Record a new timestamp for <paramref name="ruleId"/> +
    /// <paramref name="groupKey"/> and return the number of
    /// timestamps still within <paramref name="window"/> ending at
    /// <paramref name="eventTime"/> (inclusive of the new entry).
    /// </summary>
    int RecordAndCount(
        Guid ruleId,
        string groupKey,
        DateTime eventTime,
        TimeSpan window);
}

/// <summary>
/// Default in-memory implementation of <see cref="IRuleWindowStore"/>.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/> and
/// a per-bucket lock. Buckets that haven't been touched in
/// <see cref="EvictionGraceMinutes"/> minutes are pruned on the next
/// call — simple LRU-by-time so long-idle rules don't leak memory.
///
/// <para>State is process-local; a restart zeros every bucket. That
/// matches the token-bucket rate limiter on the sink (same at-most-
/// N-per-window ceiling applies after restart) and is acceptable per
/// the Wave C.2 brief — duplicate fires become at-most-one delivery
/// per throttle window.</para>
/// </summary>
public sealed class InMemoryRuleWindowStore : IRuleWindowStore
{
    /// <summary>
    /// Prune buckets idle for this many minutes on every call. Keeps
    /// the dictionary bounded without a dedicated GC thread.
    /// </summary>
    public const int EvictionGraceMinutes = 60;

    private readonly ConcurrentDictionary<(Guid, string), Bucket> _buckets = new();

    public int RecordAndCount(
        Guid ruleId,
        string groupKey,
        DateTime eventTime,
        TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(groupKey);

        var bucket = _buckets.GetOrAdd((ruleId, groupKey), _ => new Bucket());
        lock (bucket)
        {
            bucket.LastTouched = eventTime;
            bucket.Entries.Add(eventTime);

            // Drop expired entries — anything strictly older than
            // (eventTime - window).
            var cutoff = eventTime - window;
            bucket.Entries.RemoveAll(t => t < cutoff);

            // Opportunistic eviction of other long-idle buckets. Run
            // at most once per call on every ~100th invocation to
            // avoid scanning the whole dict on every hot path.
            if ((bucket.CallCount++ & 127) == 0)
            {
                var evictCutoff = eventTime -
                    TimeSpan.FromMinutes(EvictionGraceMinutes);
                foreach (var kvp in _buckets)
                {
                    if (kvp.Value.LastTouched < evictCutoff)
                    {
                        _buckets.TryRemove(kvp.Key, out _);
                    }
                }
            }

            return bucket.Entries.Count;
        }
    }

    /// <summary>
    /// Test hook — current bucket count for the (ruleId, groupKey)
    /// tuple, or -1 if the bucket doesn't exist.
    /// </summary>
    internal int GetBucketSize(Guid ruleId, string groupKey) =>
        _buckets.TryGetValue((ruleId, groupKey), out var b) ? b.Entries.Count : -1;

    private sealed class Bucket
    {
        public readonly List<DateTime> Entries = new();
        public DateTime LastTouched;
        public int CallCount;
    }
}

/// <summary>
/// No-op window store for tests / rules that don't use
/// <c>count_gte</c>. Always returns 1 (the threshold of a single-
/// occurrence rule), so if <c>count_gte</c> is accidentally evaluated
/// with this store, the predicate fires on every call — a loud bug
/// rather than a silent mis-count.
/// </summary>
public sealed class NullRuleWindowStore : IRuleWindowStore
{
    public int RecordAndCount(
        Guid ruleId,
        string groupKey,
        DateTime eventTime,
        TimeSpan window) => 1;
}
