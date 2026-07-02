using System.Collections.Concurrent;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-6 — in-memory <see cref="IEntitlementSnapshotCache"/> over a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by tenant id (precedent
/// <c>InMemoryBudgetConfigProvider</c>). A plain dictionary (rather than
/// <c>IMemoryCache</c>) gives O(1) <see cref="Flush"/> + per-tenant
/// <see cref="Invalidate"/> without the "IMemoryCache can't enumerate keys"
/// dance, and a deterministic <see cref="TimeProvider"/>-driven TTL that tests
/// can drive with a fake clock.
///
/// <para>Singleton lifetime: one cache shared across requests + the
/// invalidation listener. The default TTL is a belt-and-suspenders memory bound
/// behind event-driven invalidation, not a correctness mechanism (pinned plan
/// versions are immutable).</para>
/// </summary>
public sealed class EntitlementSnapshotCache : IEntitlementSnapshotCache
{
    /// <summary>Default TTL — a memory bound behind event invalidation.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;

    private readonly record struct Entry(ResolvedEntitlements Value, DateTimeOffset ExpiresAt);

    public EntitlementSnapshotCache(TimeProvider time)
        : this(time, DefaultTtl)
    {
    }

    /// <summary>Test-friendly ctor with an explicit TTL.</summary>
    public EntitlementSnapshotCache(TimeProvider time, TimeSpan ttl)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "TTL must be positive.");
        }

        _ttl = ttl;
    }

    public int Count => _entries.Count;

    public ResolvedEntitlements? TryGet(Guid tenantId)
    {
        if (!_entries.TryGetValue(tenantId, out var entry))
        {
            return null;
        }

        if (_time.GetUtcNow() >= entry.ExpiresAt)
        {
            // Lazy eviction on read. TryRemove(KeyValuePair) only removes when
            // the slot still holds the expired entry — a concurrent Set that
            // refreshed the entry is preserved.
            _entries.TryRemove(new KeyValuePair<Guid, Entry>(tenantId, entry));
            return null;
        }

        return entry.Value;
    }

    public void Set(Guid tenantId, ResolvedEntitlements resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        _entries[tenantId] = new Entry(resolved, _time.GetUtcNow().Add(_ttl));
    }

    public void Invalidate(Guid tenantId) => _entries.TryRemove(tenantId, out _);

    public void Flush() => _entries.Clear();
}
