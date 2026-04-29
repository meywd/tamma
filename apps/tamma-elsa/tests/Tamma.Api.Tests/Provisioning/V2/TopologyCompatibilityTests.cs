using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.V2;

namespace Tamma.Api.Tests.Provisioning.V2;

/// <summary>
/// Story 30-1 AC9: every provider × every topology pair must produce a
/// structured failure (not silent success / silent throw) when the
/// topology is unsupported. This is the contract the dispatch workflow
/// (Story 30-2) relies on to refuse incompatible requests at intake.
///
/// <para>This test fixture demonstrates the contract using a reference
/// implementation (<see cref="ContractCompliantTestProvider"/>) that the
/// real Cranl / Hetzner / Cloudflare / BYO providers (Stories 30-3..30-6)
/// will be measured against.</para>
/// </summary>
[TestFixture]
public sealed class TopologyCompatibilityTests
{
    private static IEnumerable<ProvisioningTopology> AllRealTopologies()
    {
        yield return ProvisioningTopology.DatabaseOnly;
        yield return ProvisioningTopology.DedicatedCompute;
        yield return ProvisioningTopology.Managed;
    }

    [TestCaseSource(nameof(EveryProviderAndTopology))]
    public async Task ProvisionAsync_ReturnsStructuredFailure_WhenTopologyUnsupported(
        ITenantInfrastructureProvider provider,
        ProvisioningTopology topology)
    {
        var caps = provider.GetCapabilities();

        if (caps.SupportsTopology(topology))
        {
            // Supported — happy-path is asserted in per-provider tests
            // (Stories 30-3..30-6 will land those). Skip here.
            Assert.Pass($"{provider.ProviderKey} supports {topology} — covered by per-provider happy-path tests.");
        }

        var result = await provider.ProvisionAsync(
            Guid.NewGuid(),
            new ProvisioningRequest(topology),
            CancellationToken.None);

        result.Status.State.Should().Be(ProvisioningState.Failed);
        result.Status.FailureReason.Should().Be(
            "unsupported_topology",
            "AC9 requires a structured short-code so the dispatch workflow can decide retry vs surface");
        result.Endpoints.Should().BeNull();
        result.ProviderResourceIds.Should().BeEmpty();
    }

    [Test]
    public async Task NullProvider_AlwaysThrowsNotSupported_ForAnyTopology()
    {
        // The null seam is the single exception to the AC9 "structured
        // failure not throw" rule: it advertises NO topologies (its very
        // purpose is to never run real provisioning), so calling it is a
        // configuration bug rather than a contract violation. The throw
        // is the loudest possible signal to the operator.
        var nullProvider = new NullTenantProvider();

        foreach (var topology in AllRealTopologies())
        {
            var act = async () => await nullProvider.ProvisionAsync(
                Guid.NewGuid(),
                new ProvisioningRequest(topology),
                CancellationToken.None);

            await act.Should().ThrowAsync<NotSupportedException>(
                $"null provider should refuse {topology} loudly");
        }
    }

    public static IEnumerable<TestCaseData> EveryProviderAndTopology()
    {
        var providers = ContractCompliantTestProvider.SampleMatrix();
        foreach (var provider in providers)
        {
            foreach (var topology in AllRealTopologies())
            {
                yield return new TestCaseData(provider, topology)
                    .SetName($"{provider.ProviderKey}_{topology}");
            }
        }
    }

    /// <summary>
    /// A reference implementation of the contract — every real Epic 30
    /// provider must behave the same way for unsupported topologies.
    /// Built once per topology mix so the parameterised test can iterate
    /// over the full (provider, topology) matrix without spinning up
    /// real infra.
    /// </summary>
    private sealed class ContractCompliantTestProvider : ITenantInfrastructureProvider
    {
        private readonly ProviderCapabilities _capabilities;

        public ContractCompliantTestProvider(string key, ProvisioningTopology supported)
        {
            ProviderKey = key;
            _capabilities = new ProviderCapabilities(
                ProviderKey: key,
                DisplayName: $"Contract({key})",
                SupportedTopologies: supported,
                Regions: new[] { "test-region" });
        }

        /// <summary>Build a representative sample of the Epic 30
        /// provider matrix — matches the brief's capability table:
        /// cranl=DedicatedCompute, hetzner=DatabaseOnly+DedicatedCompute,
        /// cloudflare=DatabaseOnly+DedicatedCompute, byo=Managed.</summary>
        public static IReadOnlyList<ITenantInfrastructureProvider> SampleMatrix() =>
            new ITenantInfrastructureProvider[]
            {
                new ContractCompliantTestProvider("cranl-test",
                    ProvisioningTopology.DedicatedCompute),
                new ContractCompliantTestProvider("hetzner-test",
                    ProvisioningTopology.DatabaseOnly | ProvisioningTopology.DedicatedCompute),
                new ContractCompliantTestProvider("cloudflare-test",
                    ProvisioningTopology.DatabaseOnly | ProvisioningTopology.DedicatedCompute),
                new ContractCompliantTestProvider("byo-test",
                    ProvisioningTopology.Managed),
            };

        public string ProviderKey { get; }

        public ProviderCapabilities GetCapabilities() => _capabilities;

        public Task<ProvisioningResult> ProvisionAsync(
            Guid tenantId, ProvisioningRequest request, CancellationToken ct)
        {
            // Reference implementation of AC9 — every real provider must
            // emit this exact shape on unsupported-topology.
            if (!_capabilities.SupportsTopology(request.Topology))
            {
                return Task.FromResult(new ProvisioningResult(
                    new ProvisioningStatusSnapshot(
                        ProvisioningState.Failed,
                        Detail: $"{ProviderKey} does not support topology {request.Topology}",
                        FailureReason: "unsupported_topology",
                        UpdatedAt: DateTimeOffset.UtcNow),
                    new Dictionary<string, string>()));
            }

            // Happy path — out-of-scope here; per-provider tests cover it.
            return Task.FromResult(new ProvisioningResult(
                new ProvisioningStatusSnapshot(
                    ProvisioningState.Pending, "accepted", null, DateTimeOffset.UtcNow),
                new Dictionary<string, string>()));
        }

        public Task<ProvisioningStatusSnapshot> GetStatusAsync(
            Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new ProvisioningStatusSnapshot(
                ProvisioningState.None, null, null, DateTimeOffset.UtcNow));

        public Task DeprovisionAsync(
            Guid tenantId, DeprovisioningRequest request, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<TenantEndpoints> ResolveEndpointsAsync(
            Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new TenantEndpoints("postgresql://test", null, null));
    }
}
