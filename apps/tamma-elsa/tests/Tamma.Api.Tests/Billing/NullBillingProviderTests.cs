using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Billing;
using Tamma.Core;
using Tamma.Core.Billing;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-1 (AC9) — the single-user no-op provider reports disabled and never
/// touches Stripe. The mutating methods throw a clear SaaS-only error if a
/// caller ignores <c>IsEnabled</c> (defence-in-depth; the hook never calls them
/// in single-user).
/// </summary>
[TestFixture]
public class NullBillingProviderTests
{
    [Test]
    public void IsEnabled_Is_False()
    {
        new NullBillingProvider().IsEnabled.Should().BeFalse();
    }

    [Test]
    public async Task CreateCustomerAsync_Throws_SaasOnly()
    {
        var provider = new NullBillingProvider();
        var act = async () => await provider.CreateCustomerAsync(
            Guid.NewGuid(),
            new CustomerDescriptor("Acme", "acme", "owner@example.com", BillingMode.PlatformProvided));

        var ex = await act.Should().ThrowAsync<TammaError>();
        ex.Which.Code.Should().Be("BILLING.SAAS_ONLY");
    }

    [Test]
    public async Task SyncCatalogAsync_Throws_SaasOnly()
    {
        var provider = new NullBillingProvider();
        var act = async () => await provider.SyncCatalogAsync();

        var ex = await act.Should().ThrowAsync<TammaError>();
        ex.Which.Code.Should().Be("BILLING.SAAS_ONLY");
    }
}
