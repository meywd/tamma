using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Pricing;
using Tamma.Core.Enums;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-6 (AC6, AC13) — the per-tenant snapshot cache: round-trip, evict
/// exactly one, flush all, deterministic TTL expiry (fake clock), and
/// two-tenant no-collision.
/// </summary>
[TestFixture]
public class EntitlementSnapshotCacheTests
{
    private static ResolvedEntitlements Sample(Guid tenantId) =>
        new(tenantId, Guid.NewGuid(), 1, false,
            new Dictionary<EntitlementMetricKey, ResolvedEntitlement>
            {
                [EntitlementMetricKey.Seats] = new(EntitlementMetricKey.Seats, 5, "monthly", "block"),
            });

    [Test]
    public void Set_TryGet_RoundTrips_PerTenant()
    {
        var cache = new EntitlementSnapshotCache(new PricingTestClock());
        var t = Guid.NewGuid();
        var snap = Sample(t);

        cache.Set(t, snap);

        cache.TryGet(t).Should().BeSameAs(snap);
        cache.Count.Should().Be(1);
    }

    [Test]
    public void TryGet_Miss_ReturnsNull()
    {
        var cache = new EntitlementSnapshotCache(new PricingTestClock());
        cache.TryGet(Guid.NewGuid()).Should().BeNull();
    }

    [Test]
    public void Invalidate_EvictsExactlyOne()
    {
        var cache = new EntitlementSnapshotCache(new PricingTestClock());
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        cache.Set(a, Sample(a));
        cache.Set(b, Sample(b));

        cache.Invalidate(a);

        cache.TryGet(a).Should().BeNull();
        cache.TryGet(b).Should().NotBeNull("only tenant A was evicted");
        cache.Count.Should().Be(1);
    }

    [Test]
    public void Flush_ClearsAll()
    {
        var cache = new EntitlementSnapshotCache(new PricingTestClock());
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        cache.Set(a, Sample(a));
        cache.Set(b, Sample(b));

        cache.Flush();

        cache.Count.Should().Be(0);
        cache.TryGet(a).Should().BeNull();
        cache.TryGet(b).Should().BeNull();
    }

    [Test]
    public void Ttl_Expiry_EvictsOnRead()
    {
        var clock = new PricingTestClock();
        var cache = new EntitlementSnapshotCache(clock, TimeSpan.FromMinutes(5));
        var t = Guid.NewGuid();
        cache.Set(t, Sample(t));

        clock.Advance(TimeSpan.FromMinutes(4));
        cache.TryGet(t).Should().NotBeNull("still within TTL");

        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        cache.TryGet(t).Should().BeNull("TTL elapsed");
        cache.Count.Should().Be(0, "expired entry evicted lazily on read");
    }

    [Test]
    public void TwoTenants_DoNotCollide()
    {
        var cache = new EntitlementSnapshotCache(new PricingTestClock());
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var snapA = Sample(a);
        var snapB = Sample(b);
        cache.Set(a, snapA);
        cache.Set(b, snapB);

        cache.TryGet(a).Should().BeSameAs(snapA);
        cache.TryGet(b).Should().BeSameAs(snapB);
    }
}
