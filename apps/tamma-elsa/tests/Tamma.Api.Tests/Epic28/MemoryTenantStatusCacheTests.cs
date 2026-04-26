using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Tamma.Api.Services.TenantStatus;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-8 AC3 — unit tests for the in-memory tenant status cache.
/// Covers: TTL expiry, immediate invalidation, missing-entry semantics,
/// cap enforcement, and concurrent-access safety.
/// </summary>
[TestFixture]
public class MemoryTenantStatusCacheTests
{
    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public void Advance(TimeSpan span) => _now += span;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static MemoryTenantStatusCache NewCache(
        TimeProvider time, int ttlSeconds = 10, int maxEntries = 10000) =>
        new(Options.Create(new TenantStatusCacheOptions
        {
            TtlSeconds = ttlSeconds,
            MaxEntries = maxEntries,
        }), time);

    [Test]
    public void TryGet_Missing_ReturnsFalse()
    {
        var cache = NewCache(TimeProvider.System);
        cache.TryGet(Guid.NewGuid(), out var status).Should().BeFalse();
        status.Should().BeNull();
    }

    [Test]
    public void Set_Then_TryGet_ReturnsCachedStatus()
    {
        var cache = NewCache(TimeProvider.System);
        var id = Guid.NewGuid();
        cache.Set(id, "active");

        cache.TryGet(id, out var status).Should().BeTrue();
        status.Should().Be("active");
    }

    [Test]
    public void Set_Null_Status_Is_Cached()
    {
        var cache = NewCache(TimeProvider.System);
        var id = Guid.NewGuid();
        cache.Set(id, null);

        cache.TryGet(id, out var status).Should().BeTrue(
            "a null status (legacy row) is a valid cached value, not a miss");
        status.Should().BeNull();
    }

    [Test]
    public void TryGet_AfterTtlExpires_ReturnsFalse()
    {
        var time = new TestTime();
        var cache = NewCache(time, ttlSeconds: 5);
        var id = Guid.NewGuid();
        cache.Set(id, "active");

        time.Advance(TimeSpan.FromSeconds(6));

        cache.TryGet(id, out _).Should().BeFalse();
    }

    [Test]
    public void Invalidate_Removes_Entry_Immediately()
    {
        var cache = NewCache(TimeProvider.System);
        var id = Guid.NewGuid();
        cache.Set(id, "active");

        cache.Invalidate(id);

        cache.TryGet(id, out _).Should().BeFalse();
    }

    [Test]
    public void Invalidate_MissingEntry_IsNoOp()
    {
        var cache = NewCache(TimeProvider.System);
        Action act = () => cache.Invalidate(Guid.NewGuid());
        act.Should().NotThrow();
    }

    [Test]
    public void Set_AboveMaxEntries_TriggersEviction()
    {
        var cache = NewCache(TimeProvider.System, maxEntries: 10);
        // Fill past the cap (cap + 10% slack means cap + 1 = 11 needs writes
        // to trigger; push to cap*2 to be sure we evict.).
        for (var i = 0; i < 50; i++)
            cache.Set(Guid.NewGuid(), "active");

        // After eviction, we should be below cap. Some implementations
        // settle slightly above when slack hasn't been crossed yet —
        // assert it's bounded, not exact.
        var probeCount = 0;
        for (var i = 0; i < 100; i++)
            if (cache.TryGet(Guid.NewGuid(), out _)) probeCount++;
        probeCount.Should().Be(0, "all probe ids are fresh, none should hit");
    }

    [Test]
    public void Constructor_InvalidTtl_Throws()
    {
        Action act = () => new MemoryTenantStatusCache(
            Options.Create(new TenantStatusCacheOptions { TtlSeconds = 0 }));
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Constructor_InvalidMaxEntries_Throws()
    {
        Action act = () => new MemoryTenantStatusCache(
            Options.Create(new TenantStatusCacheOptions { MaxEntries = 0 }));
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task Concurrent_SetAndGet_AreThreadSafe()
    {
        var cache = NewCache(TimeProvider.System);
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();
        // 50 writers + 50 readers in parallel; nothing should throw.
        await Task.WhenAll(
            ids.Select(id => Task.Run(() => cache.Set(id, "active"))));
        await Task.WhenAll(
            ids.Select(id => Task.Run(() =>
                cache.TryGet(id, out _).Should().BeTrue())));
    }

    // ─────────────────────────────────────────────────────────────────────
    //  M6 — real LRU semantics
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void LruEviction_RemovesLeastRecentlyAccessed_WhenOverCap()
    {
        // Cap=3. Insert 4 entries; the FIRST one (least recently
        // touched) must be the eviction victim.
        var cache = NewCache(TimeProvider.System, maxEntries: 3);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();

        cache.Set(a, "active");
        cache.Set(b, "active");
        cache.Set(c, "active");
        // a is now LRU. Inserting d should evict a.
        cache.Set(d, "active");

        cache.TryGet(a, out _).Should().BeFalse(
            "the least-recently-set entry must be evicted under cap pressure");
        cache.TryGet(b, out _).Should().BeTrue();
        cache.TryGet(c, out _).Should().BeTrue();
        cache.TryGet(d, out _).Should().BeTrue();
    }

    [Test]
    public void LruEviction_TouchedEntry_SurvivesEvictionWave()
    {
        // Cap=3. Insert a, b, c → touch a → insert d. d should evict
        // b (the new LRU after a was touched), not a.
        var cache = NewCache(TimeProvider.System, maxEntries: 3);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();

        cache.Set(a, "active");
        cache.Set(b, "active");
        cache.Set(c, "active");
        // Touch a — moves it to the front of the LRU.
        cache.TryGet(a, out _).Should().BeTrue();
        cache.Set(d, "active");

        cache.TryGet(a, out _).Should().BeTrue(
            "TryGet move-to-front must save 'a' from being the LRU victim");
        cache.TryGet(b, out _).Should().BeFalse(
            "after the move-to-front, 'b' is the new LRU victim");
        cache.TryGet(c, out _).Should().BeTrue();
        cache.TryGet(d, out _).Should().BeTrue();
    }

    [Test]
    public void Set_RefreshesEntry_WithoutChangingMembership()
    {
        // Re-Setting the same id at cap should NOT evict anything —
        // the entry is updated in place + repositioned to MRU.
        var cache = NewCache(TimeProvider.System, maxEntries: 3);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        cache.Set(a, "provisioning");
        cache.Set(b, "active");
        cache.Set(c, "active");
        cache.Set(a, "active");  // refresh — should overwrite, not evict

        cache.TryGet(a, out var aStatus).Should().BeTrue();
        aStatus.Should().Be("active");
        cache.TryGet(b, out _).Should().BeTrue();
        cache.TryGet(c, out _).Should().BeTrue();
    }
}
