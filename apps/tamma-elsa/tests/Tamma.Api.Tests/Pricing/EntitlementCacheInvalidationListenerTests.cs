using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.PlatformEvents;
using Tamma.Api.Services.Pricing;
using Tamma.Core.Enums;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-6 (AC6, AC13) — the event-driven cache-invalidation listener over
/// the real in-process <see cref="InMemoryPlatformEventBus"/>:
/// <c>TENANT.PLAN.CHANGED</c> evicts exactly one tenant, a catalog edit flushes
/// all, and a malformed event evicts NOTHING (never a blanket flush).
/// </summary>
[TestFixture]
public class EntitlementCacheInvalidationListenerTests
{
    private static ResolvedEntitlements Sample(Guid t) =>
        new(t, Guid.NewGuid(), 1, false,
            new Dictionary<EntitlementMetricKey, ResolvedEntitlement>
            {
                [EntitlementMetricKey.Seats] = new(EntitlementMetricKey.Seats, 1, "monthly", "block"),
            });

    private static (InMemoryPlatformEventBus bus, EntitlementSnapshotCache cache,
        EntitlementCacheInvalidationListener listener) NewHarness()
    {
        var bus = new InMemoryPlatformEventBus(NullLogger<InMemoryPlatformEventBus>.Instance);
        var cache = new EntitlementSnapshotCache(new PricingTestClock());
        var listener = new EntitlementCacheInvalidationListener(
            bus, cache, NullLogger<EntitlementCacheInvalidationListener>.Instance);
        return (bus, cache, listener);
    }

    [Test]
    public async Task TenantPlanChanged_EvictsExactlyThatTenant_ViaColumn()
    {
        var (bus, cache, listener) = NewHarness();
        await listener.StartAsync(CancellationToken.None);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        cache.Set(a, Sample(a));
        cache.Set(b, Sample(b));

        await bus.PublishAsync(new PlatformEvent { Type = "TENANT.PLAN.CHANGED", TenantId = a });

        cache.TryGet(a).Should().BeNull("tenant A's plan changed → evicted");
        cache.TryGet(b).Should().NotBeNull("tenant B untouched");

        await listener.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task TenantPlanChanged_EvictsViaTenantIdTag_WhenColumnEmpty()
    {
        var (bus, cache, listener) = NewHarness();
        await listener.StartAsync(CancellationToken.None);

        var a = Guid.NewGuid();
        cache.Set(a, Sample(a));

        await bus.PublishAsync(new PlatformEvent
        {
            Type = "TENANT.PLAN.CHANGED",
            TenantId = null,
            Tags = JsonSerializer.Serialize(new { tenantId = a.ToString() }),
        });

        cache.TryGet(a).Should().BeNull("tenantId read from the tag fallback");
        await listener.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task CatalogEdit_FlushesAll()
    {
        var (bus, cache, listener) = NewHarness();
        await listener.StartAsync(CancellationToken.None);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        cache.Set(a, Sample(a));
        cache.Set(b, Sample(b));

        await bus.PublishAsync(new PlatformEvent { Type = "PLAN.VERSION.CREATED" });

        cache.Count.Should().Be(0, "a catalog edit flushes the whole cache");
        await listener.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task MalformedTenantPlanEvent_EvictsNothing()
    {
        var (bus, cache, listener) = NewHarness();
        await listener.StartAsync(CancellationToken.None);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        cache.Set(a, Sample(a));
        cache.Set(b, Sample(b));

        // No TenantId column, no tenantId tag → resolves to nothing.
        await bus.PublishAsync(new PlatformEvent
        {
            Type = "TENANT.PLAN.CHANGED",
            TenantId = null,
            Tags = "{}",
        });

        cache.Count.Should().Be(2, "a malformed event evicts nothing — never a blanket flush");
        await listener.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task EntitlementResolvedEvents_DoNotTriggerInvalidation()
    {
        var (bus, cache, listener) = NewHarness();
        await listener.StartAsync(CancellationToken.None);

        var a = Guid.NewGuid();
        cache.Set(a, Sample(a));

        // ENTITLEMENT.* is neither TENANT.PLAN.* nor PLAN.* — no feedback loop.
        await bus.PublishAsync(new PlatformEvent
        {
            Type = EntitlementEventTypes.ResolvedSuccess, TenantId = a,
        });

        cache.TryGet(a).Should().NotBeNull("resolve events must not evict");
        await listener.StopAsync(CancellationToken.None);
    }
}
