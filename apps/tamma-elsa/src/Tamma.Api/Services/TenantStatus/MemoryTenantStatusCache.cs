using Microsoft.Extensions.Options;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Services.TenantStatus;

/// <summary>
/// In-memory <see cref="ITenantStatusCache"/> backed by a true LRU
/// (doubly-linked list ordered MRU→LRU + dictionary for O(1) lookup).
///
/// <para>Hot path is locked rather than lock-free because TryGet must
/// perform a move-to-front under the same critical section that reads
/// the entry — without that, two concurrent <c>TryGet</c>s could leave
/// the linked list in an inconsistent state. The lock is short
/// (single linked-list reposition) and the cache is per-pod, so the
/// contention envelope is bounded by per-pod request concurrency.</para>
///
/// <para>Coherence: per-pod only. <see cref="Invalidate"/> drops the
/// entry on this pod immediately; sibling pods converge after the TTL.
/// Acceptable for the 10-second-tier caching this story targets.</para>
/// </summary>
public sealed class MemoryTenantStatusCache : ITenantStatusCache, ITenantStatusProbe
{
    private readonly TimeProvider _time;
    private readonly TenantStatusCacheOptions _options;

    /// <summary>
    /// LRU node payload — the cached status + expiry. The list orders
    /// these MRU-first; the dictionary maps tenant id to the list node
    /// for O(1) reposition on access.
    /// </summary>
    private sealed class CacheEntry
    {
        public required Guid TenantId { get; init; }
        public string? Status { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private readonly Dictionary<Guid, LinkedListNode<CacheEntry>> _entries = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly object _lock = new();

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
        lock (_lock)
        {
            if (!_entries.TryGetValue(tenantId, out var node))
            {
                status = null;
                return false;
            }

            if (node.Value.ExpiresAt <= _time.GetUtcNow())
            {
                // Expired — drop and report miss so caller re-fetches.
                _entries.Remove(tenantId);
                _lru.Remove(node);
                status = null;
                return false;
            }

            // Hot-path access: move-to-front so this entry survives the
            // next eviction wave.
            _lru.Remove(node);
            _lru.AddFirst(node);
            status = node.Value.Status;
            return true;
        }
    }

    public void Set(Guid tenantId, string? status)
    {
        var expiry = _time.GetUtcNow().AddSeconds(_options.TtlSeconds);

        lock (_lock)
        {
            if (_entries.TryGetValue(tenantId, out var existing))
            {
                // Refresh in place + reposition.
                existing.Value.Status = status;
                existing.Value.ExpiresAt = expiry;
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var entry = new CacheEntry
            {
                TenantId = tenantId,
                Status = status,
                ExpiresAt = expiry,
            };
            var node = new LinkedListNode<CacheEntry>(entry);
            _lru.AddFirst(node);
            _entries[tenantId] = node;

            // Evict the least-recently-used entry while we're over the
            // cap. Real LRU: the list's last node is by definition the
            // entry that was accessed least recently.
            while (_lru.Count > _options.MaxEntries)
            {
                var victim = _lru.Last;
                if (victim is null) break;
                _lru.RemoveLast();
                _entries.Remove(victim.Value.TenantId);
            }
        }
    }

    public void Invalidate(Guid tenantId)
    {
        lock (_lock)
        {
            if (_entries.Remove(tenantId, out var node))
            {
                _lru.Remove(node);
            }
        }
    }
}
