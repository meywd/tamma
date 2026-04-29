using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.V2;

namespace Tamma.Api.Tests.Provisioning.V2;

/// <summary>
/// Story 30-1 AC10: "no switch statement anywhere on provider key outside
/// the registry itself." These tests pin down the registry's lookup +
/// duplicate-key + capability-listing contract so 30-2..30-10 can take a
/// hard dependency on it.
/// </summary>
[TestFixture]
public sealed class TenantProviderRegistryTests
{
    private static readonly Guid AnyTenantId = Guid.NewGuid();

    [Test]
    public void GetProvider_ReturnsRegisteredProviderByKey()
    {
        var nullProvider = new NullTenantProvider();
        var fakeProvider = new FakeProvider("fake", ProvisioningTopology.DatabaseOnly);
        var registry = new TenantProviderRegistry(new ITenantInfrastructureProvider[]
        {
            nullProvider, fakeProvider
        });

        registry.GetProvider("null").Should().BeSameAs(nullProvider);
        registry.GetProvider("fake").Should().BeSameAs(fakeProvider);
    }

    [Test]
    public void GetProvider_ThrowsForUnknownKey()
    {
        var registry = new TenantProviderRegistry(new[] { (ITenantInfrastructureProvider)new NullTenantProvider() });

        var act = () => registry.GetProvider("does-not-exist");

        act.Should().Throw<KeyNotFoundException>()
            .WithMessage("*does-not-exist*Registered keys*");
    }

    [Test]
    public void GetProvider_ThrowsForBlankKey()
    {
        var registry = new TenantProviderRegistry(new[] { (ITenantInfrastructureProvider)new NullTenantProvider() });

        ((Action)(() => registry.GetProvider(""))).Should().Throw<ArgumentException>();
        ((Action)(() => registry.GetProvider("   "))).Should().Throw<ArgumentException>();
    }

    [Test]
    public void Constructor_ThrowsOnDuplicateKey()
    {
        var act = () => new TenantProviderRegistry(new ITenantInfrastructureProvider[]
        {
            new FakeProvider("dup", ProvisioningTopology.DatabaseOnly),
            new FakeProvider("dup", ProvisioningTopology.DedicatedCompute),
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate provider key 'dup'*");
    }

    [Test]
    public void Constructor_ThrowsOnEmptyKey()
    {
        var act = () => new TenantProviderRegistry(new ITenantInfrastructureProvider[]
        {
            new FakeProvider("", ProvisioningTopology.DatabaseOnly),
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty ProviderKey*");
    }

    [Test]
    public void TryGetProvider_ReturnsFalseForUnknownKey()
    {
        var registry = new TenantProviderRegistry(new[] { (ITenantInfrastructureProvider)new NullTenantProvider() });

        registry.TryGetProvider("missing", out var resolved).Should().BeFalse();
        resolved.Should().BeNull();

        registry.TryGetProvider("null", out var nullSeam).Should().BeTrue();
        nullSeam.Should().NotBeNull();
    }

    [Test]
    public void ListCapabilities_ReturnsCapabilitiesForEveryRegisteredProvider()
    {
        var registry = new TenantProviderRegistry(new ITenantInfrastructureProvider[]
        {
            new NullTenantProvider(),
            new FakeProvider("fake-a", ProvisioningTopology.DatabaseOnly),
            new FakeProvider("fake-b", ProvisioningTopology.DedicatedCompute | ProvisioningTopology.Managed),
        });

        var caps = registry.ListCapabilities();

        caps.Should().HaveCount(3);
        caps.Should().Contain(c => c.ProviderKey == "null");
        caps.Should().Contain(c => c.ProviderKey == "fake-a");
        caps.Should().Contain(c => c.ProviderKey == "fake-b"
            && c.SupportsTopology(ProvisioningTopology.DedicatedCompute)
            && c.SupportsTopology(ProvisioningTopology.Managed)
            && !c.SupportsTopology(ProvisioningTopology.DatabaseOnly));
    }

    [Test]
    public void RegisteredKeys_ListsEveryKey()
    {
        var registry = new TenantProviderRegistry(new ITenantInfrastructureProvider[]
        {
            new NullTenantProvider(),
            new FakeProvider("alpha", ProvisioningTopology.DatabaseOnly),
        });

        registry.RegisteredKeys.Should().BeEquivalentTo(new[] { "null", "alpha" });
    }

    /// <summary>Minimal in-memory provider for registry tests — does not
    /// implement real provisioning, only the surface the registry
    /// exercises (key + capabilities).</summary>
    private sealed class FakeProvider : ITenantInfrastructureProvider
    {
        private readonly ProviderCapabilities _capabilities;

        public FakeProvider(string key, ProvisioningTopology supported)
        {
            ProviderKey = key;
            _capabilities = new ProviderCapabilities(
                ProviderKey: key,
                DisplayName: $"Fake({key})",
                SupportedTopologies: supported,
                Regions: new[] { "fake-region-1" });
        }

        public string ProviderKey { get; }

        public ProviderCapabilities GetCapabilities() => _capabilities;

        public Task<ProvisioningResult> ProvisionAsync(
            Guid tenantId, ProvisioningRequest request, CancellationToken ct) =>
            Task.FromResult(new ProvisioningResult(
                new ProvisioningStatusSnapshot(
                    ProvisioningState.Pending, null, null, DateTimeOffset.UtcNow),
                new Dictionary<string, string>()));

        public Task<ProvisioningStatusSnapshot> GetStatusAsync(
            Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new ProvisioningStatusSnapshot(
                ProvisioningState.None, null, null, DateTimeOffset.UtcNow));

        public Task DeprovisionAsync(
            Guid tenantId, DeprovisioningRequest request, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<TenantEndpoints> ResolveEndpointsAsync(
            Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new TenantEndpoints("postgresql://fake", null, null));
    }
}
