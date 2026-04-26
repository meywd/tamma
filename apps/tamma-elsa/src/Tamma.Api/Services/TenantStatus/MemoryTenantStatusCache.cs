using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Tamma.Api.Services.TenantStatus;

/// <summary>
/// In-memory <see cref="ITenantStatusCache"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> of expiry-tagged
/// entries. Lock-free hot path; eviction runs lazily on read when the
/// cache exceeds <see cref="TenantStatusCacheOptions.MaxEntries"/>.
///
/// <para>Coherence: per-pod only. <see cref="Invalidate"/> drops the
/// entry on this pod immediately; sibling pods converge after the TTL.
/// Acceptable for the 10-second-tier caching this story targets.</para>
/// </summary>
public sealed class MemoryTenantStatusCache : ITenantStatusCache
{
    private readonly TimeProvider _time;
    private readonly TenantStatusCacheOptions _options;

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    private readonly record struct Entry(string? Status, DateTimeOffset ExpiresAt);

    public MemoryTenantStatusCache(
        IOptions<TenantStatusCacheOptions> options,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _time = time ?? TimeProvider.System;

        if (_options.TtlSeconds <= 0)
            throw new ArgumentException(
                $"TenantStatusCache:TtlSeconds must be > 0 (got {_options.TtlSeconds}).",
                nameof(options));
        if (_options.MaxEntries <= 0)
            throw new ArgumentException(
                $"TenantStatusCache:MaxEntries must be > 0 (got {_options.MaxEntries}).",
                nameof(options));
    }

    public bool TryGet(Guid tenantId, out string? status)
    {
        if (_entries.TryGetValue(tenantId, out var entry)
            && entry.ExpiresAt > _time.GetUtcNow())
        {
            status = entry.Status;
            return true;
        }
        // Lazily drop expired entries on miss so the cache doesn't grow
        // unbounded between Set calls. _entries.TryRemove is no-op on
        // already-evicted keys — safe across races.
        _entries.TryRemove(tenantId, out _);
        status = null;
        return false;
    }

    public void Set(Guid tenantId, string? status)
    {
        var expiry = _time.GetUtcNow()
            .AddSeconds(_options.TtlSeconds);
        _entries[tenantId] = new Entry(status, expiry);

        // Lazy cap enforcement. Triggered when we're well past the
        // configured limit so we don't reap one entry per write.
        if (_entries.Count > _options.MaxEntries * 11 / 10)
        {
            EvictExpired();
            // If still over the cap (everything is fresh), evict
            // arbitrary entries until we're back under.
            if (_entries.Count > _options.MaxEntries)
            {
                var overflow = _entries.Count - _options.MaxEntries;
                foreach (var key in _entries.Keys.Take(overflow))
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }
    }

    public void Invalidate(Guid tenantId)
    {
        _entries.TryRemove(tenantId, out _);
    }

    private void EvictExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var kv in _entries)
        {
            if (kv.Value.ExpiresAt <= now)
                _entries.TryRemove(kv.Key, out _);
        }
    }
}
