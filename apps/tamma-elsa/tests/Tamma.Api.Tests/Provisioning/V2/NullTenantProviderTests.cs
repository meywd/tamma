using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.V2;

namespace Tamma.Api.Tests.Provisioning.V2;

/// <summary>
/// Behavioural contract for <see cref="NullTenantProvider"/> — Story 30-1.
/// The null seam is the only provider wired in single-user mode and the
/// baseline registration in tests. It MUST refuse provisioning + endpoint
/// resolution loudly (NotSupportedException) and MUST return a stable
/// status snapshot so health-check enumerators don't have to special-case
/// it.
/// </summary>
[TestFixture]
public sealed class NullTenantProviderTests
{
    private NullTenantProvider _provider = null!;

    [SetUp]
    public void SetUp() => _provider = new NullTenantProvider();

    [Test]
    public void ProviderKey_IsNullSentinel()
    {
        _provider.ProviderKey.Should().Be("null");
        NullTenantProvider.Key.Should().Be("null");
    }

    [Test]
    public void GetCapabilities_AdvertisesNoTopologies()
    {
        var caps = _provider.GetCapabilities();

        caps.ProviderKey.Should().Be("null");
        caps.SupportedTopologies.Should().Be(ProvisioningTopology.None);
        caps.Regions.Should().BeEmpty();
        caps.SupportsTopology(ProvisioningTopology.DatabaseOnly).Should().BeFalse();
        caps.SupportsTopology(ProvisioningTopology.DedicatedCompute).Should().BeFalse();
        caps.SupportsTopology(ProvisioningTopology.Managed).Should().BeFalse();
    }

    [Test]
    public async Task GetStatusAsync_ReturnsStableNoneSnapshot()
    {
        var snap = await _provider.GetStatusAsync(Guid.NewGuid(), CancellationToken.None);

        snap.State.Should().Be(ProvisioningState.None);
        snap.Detail.Should().Be("null_provider_no_state");
        snap.FailureReason.Should().BeNull();
    }

    [Test]
    public async Task ProvisionAsync_ThrowsNotSupported()
    {
        var act = async () => await _provider.ProvisionAsync(
            Guid.NewGuid(),
            new ProvisioningRequest(ProvisioningTopology.DatabaseOnly),
            CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Test]
    public async Task DeprovisionAsync_ThrowsNotSupported()
    {
        var act = async () => await _provider.DeprovisionAsync(
            Guid.NewGuid(),
            new DeprovisioningRequest(),
            CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Test]
    public async Task ResolveEndpointsAsync_ThrowsNotSupported()
    {
        var act = async () => await _provider.ResolveEndpointsAsync(
            Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
    }
}
