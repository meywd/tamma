using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Pricing;
using Tamma.Core.Enums;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-3 (Reader A repoint) — <see cref="TenantProviderBillingPricingModeResolver"/>
/// reads the authoritative <c>TenantProviderBilling</c> owner (via
/// <see cref="ITenantProviderBillingResolver"/>) behind the unchanged
/// <see cref="ITenantProviderPricingModeResolver"/> seam and maps the shared
/// <see cref="MetricBillingMode"/> token to the engine's <see cref="PricingMode"/>.
/// </summary>
[TestFixture]
public class TenantProviderBillingPricingModeResolverTests
{
    private sealed class StubOwner : ITenantProviderBillingResolver
    {
        private readonly MetricBillingMode _mode;
        public StubOwner(MetricBillingMode mode) => _mode = mode;
        public Guid? SeenTenant { get; private set; }
        public string? SeenProvider { get; private set; }

        public Task<MetricBillingMode> ResolveModeAsync(
            Guid? tenantId, string provider, CancellationToken ct = default)
        {
            SeenTenant = tenantId;
            SeenProvider = provider;
            return Task.FromResult(_mode);
        }
    }

    [Test]
    public async Task ResolveMode_Byok_MapsToByokPricingMode()
    {
        var resolver = new TenantProviderBillingPricingModeResolver(
            new StubOwner(MetricBillingMode.Byok));
        (await resolver.ResolveModeAsync(Guid.NewGuid(), "anthropic")).Should().Be(PricingMode.Byok);
    }

    [Test]
    public async Task ResolveMode_Platform_MapsToPlatformProvidedPricingMode()
    {
        var resolver = new TenantProviderBillingPricingModeResolver(
            new StubOwner(MetricBillingMode.PlatformProvided));
        (await resolver.ResolveModeAsync(Guid.NewGuid(), "anthropic"))
            .Should().Be(PricingMode.PlatformProvided);
    }

    [Test]
    public async Task ResolveMode_DelegatesTheTenantAndProviderToTheOwner()
    {
        var owner = new StubOwner(MetricBillingMode.Byok);
        var resolver = new TenantProviderBillingPricingModeResolver(owner);
        var tenantId = Guid.NewGuid();

        await resolver.ResolveModeAsync(tenantId, "openai");

        owner.SeenTenant.Should().Be(tenantId);
        owner.SeenProvider.Should().Be("openai");
    }
}
