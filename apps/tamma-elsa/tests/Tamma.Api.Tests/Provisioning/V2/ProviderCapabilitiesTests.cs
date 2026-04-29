using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning.V2;

namespace Tamma.Api.Tests.Provisioning.V2;

/// <summary>
/// Unit-level checks on the capability matrix — the values that drive the
/// onboarding UI's filter (Story 30-7) and the dispatch workflow's
/// pre-provision validation (Story 30-2).
/// </summary>
[TestFixture]
public sealed class ProviderCapabilitiesTests
{
    [Test]
    public void None_ReturnsCapabilitiesWithNoTopologies()
    {
        var caps = ProviderCapabilities.None("xyz", "Display Xyz");

        caps.ProviderKey.Should().Be("xyz");
        caps.DisplayName.Should().Be("Display Xyz");
        caps.SupportedTopologies.Should().Be(ProvisioningTopology.None);
        caps.Regions.Should().BeEmpty();
        caps.Features.Should().Be(ProviderFeatures.None);
        caps.MaxTenantsPerOrg.Should().BeNull();
        caps.CostHint.Should().BeNull();
    }

    [Test]
    public void SupportsTopology_ReturnsFalseForNoneInput()
    {
        var caps = new ProviderCapabilities(
            "p", "P",
            ProvisioningTopology.DatabaseOnly | ProvisioningTopology.DedicatedCompute,
            new[] { "r1" });

        caps.SupportsTopology(ProvisioningTopology.None).Should().BeFalse();
    }

    [Test]
    public void SupportsTopology_RecognisesEachComposedFlag()
    {
        var caps = new ProviderCapabilities(
            "multi", "Multi",
            ProvisioningTopology.DatabaseOnly | ProvisioningTopology.DedicatedCompute,
            new[] { "r1" });

        caps.SupportsTopology(ProvisioningTopology.DatabaseOnly).Should().BeTrue();
        caps.SupportsTopology(ProvisioningTopology.DedicatedCompute).Should().BeTrue();
        caps.SupportsTopology(ProvisioningTopology.Managed).Should().BeFalse();
    }

    [Test]
    public void Features_ComposeAsBitFlags()
    {
        var combined = ProviderFeatures.CustomDomains
            | ProviderFeatures.DedicatedDb
            | ProviderFeatures.BackupManagement;

        combined.HasFlag(ProviderFeatures.CustomDomains).Should().BeTrue();
        combined.HasFlag(ProviderFeatures.DedicatedDb).Should().BeTrue();
        combined.HasFlag(ProviderFeatures.BackupManagement).Should().BeTrue();
        combined.HasFlag(ProviderFeatures.AutoscaleCompute).Should().BeFalse();
    }

    [Test]
    public void CostHint_DefaultsToUsdCurrency()
    {
        var hint = new ProviderCostHint(12.50m);

        hint.UnitsPerMonth.Should().Be(12.50m);
        hint.Currency.Should().Be("USD");
    }
}
