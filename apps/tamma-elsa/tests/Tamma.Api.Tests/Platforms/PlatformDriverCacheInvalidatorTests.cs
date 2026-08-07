using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.PlatformEvents;
using Tamma.Api.Services.Platforms;
using Tamma.Data.Entities;
using Tamma.Platforms;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Tests.Platforms;

/// <summary>
/// Epic 31 P2 — the event-driven driver-cache invalidation SUBSCRIBER Story
/// 31-2 designed but never built. Wires a REAL <see cref="InMemoryPlatformEventBus"/>
/// to a REAL <see cref="PlatformDriverCache"/> through
/// <see cref="PlatformDriverCacheInvalidator"/> and asserts: a
/// CREDENTIAL_ROTATED / DISCONNECTED / SWITCH_ORG event evicts the tenant's
/// cached drivers IMMEDIATELY; unrelated events and other tenants' entries
/// survive.
/// </summary>
[TestFixture]
public class PlatformDriverCacheInvalidatorTests
{
    private sealed class StubDriver : IGitPlatformDriver
    {
        public PlatformKind Kind => PlatformKind.GitHub;
        public IGitPlatformClient Client { get; } = NullGitPlatformDriver.Instance.Client;
        public IGitPlatformActionsClient? Actions => null;
        public IReadOnlySet<PlatformCapability> Capabilities { get; } = new HashSet<PlatformCapability>();
    }

    private static (InMemoryPlatformEventBus Bus, PlatformDriverCache Cache, PlatformDriverCacheInvalidator Invalidator)
        Build()
    {
        var bus = new InMemoryPlatformEventBus(NullLogger<InMemoryPlatformEventBus>.Instance);
        var cache = new PlatformDriverCache(new PlatformDriverCacheOptions { MaxEntries = 16 });
        var invalidator = new PlatformDriverCacheInvalidator(
            bus, cache, NullLogger<PlatformDriverCacheInvalidator>.Instance);
        invalidator.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return (bus, cache, invalidator);
    }

    private static PlatformEvent Evt(string type, Guid? tenantId) => new()
    {
        Type = type,
        TenantId = tenantId,
        Tags = "{}",
        Metadata = "{}",
        Data = "{}",
    };

    [TestCase(PlatformInstallationEventTypes.CredentialRotated)]
    [TestCase(PlatformInstallationEventTypes.Disconnected)]
    [TestCase(PlatformInstallationEventTypes.SwitchOrg)]
    public async Task InvalidatingEvent_EvictsTheTenantsCachedDrivers_Immediately(string eventType)
    {
        var (bus, cache, _) = Build();
        var tenant = Guid.NewGuid();
        cache.Set(tenant, PlatformKind.GitHub, new StubDriver());
        cache.Set(tenant, PlatformKind.Gitea, new StubDriver());
        cache.TryGet(tenant, PlatformKind.GitHub, out _).Should().BeTrue("precondition");

        await bus.PublishAsync(Evt(eventType, tenant));

        cache.TryGet(tenant, PlatformKind.GitHub, out _).Should().BeFalse(
            "a rotated/disconnected credential must stop being used immediately, not after the TTL");
        cache.TryGet(tenant, PlatformKind.Gitea, out _).Should().BeFalse(
            "every kind for the tenant is evicted");
    }

    [Test]
    public async Task UnrelatedEvent_DoesNotEvict()
    {
        var (bus, cache, _) = Build();
        var tenant = Guid.NewGuid();
        cache.Set(tenant, PlatformKind.GitHub, new StubDriver());

        await bus.PublishAsync(Evt(PlatformInstallationEventTypes.Connected, tenant));
        await bus.PublishAsync(Evt("TENANT.CREATED.SUCCESS", tenant));

        cache.TryGet(tenant, PlatformKind.GitHub, out _).Should().BeTrue(
            "only the three invalidation event types evict");
    }

    [Test]
    public async Task OtherTenantsEntries_Survive()
    {
        var (bus, cache, _) = Build();
        var rotated = Guid.NewGuid();
        var bystander = Guid.NewGuid();
        cache.Set(rotated, PlatformKind.GitHub, new StubDriver());
        cache.Set(bystander, PlatformKind.GitHub, new StubDriver());

        await bus.PublishAsync(Evt(PlatformInstallationEventTypes.CredentialRotated, rotated));

        cache.TryGet(rotated, PlatformKind.GitHub, out _).Should().BeFalse();
        cache.TryGet(bystander, PlatformKind.GitHub, out _).Should().BeTrue(
            "invalidation is tenant-scoped");
    }

    [Test]
    public async Task TenantlessEvent_IsIgnored()
    {
        var (bus, cache, _) = Build();
        var tenant = Guid.NewGuid();
        cache.Set(tenant, PlatformKind.GitHub, new StubDriver());

        await bus.PublishAsync(Evt(PlatformInstallationEventTypes.Disconnected, tenantId: null));

        cache.TryGet(tenant, PlatformKind.GitHub, out _).Should().BeTrue(
            "no tenant on the event ⇒ nothing to scope an eviction to");
    }

    [Test]
    public async Task StoppedInvalidator_UnsubscribesFromTheBus()
    {
        var (bus, cache, invalidator) = Build();
        var tenant = Guid.NewGuid();
        cache.Set(tenant, PlatformKind.GitHub, new StubDriver());

        await invalidator.StopAsync(CancellationToken.None);
        await bus.PublishAsync(Evt(PlatformInstallationEventTypes.CredentialRotated, tenant));

        cache.TryGet(tenant, PlatformKind.GitHub, out _).Should().BeTrue(
            "a stopped host must not leave a dangling subscription");
    }
}
