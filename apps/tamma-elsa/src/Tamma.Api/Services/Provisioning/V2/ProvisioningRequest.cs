namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// The intake shape for
/// <see cref="ITenantInfrastructureProvider.ProvisionAsync"/>. Owned by the
/// dispatch workflow (Story 30-2) — the onboarding UI (Story 30-7) builds
/// it from a (backend, topology) pick + per-provider config.
/// </summary>
/// <param name="Topology">Which infrastructure shape the operator wants.
/// MUST match a flag in
/// <see cref="ProviderCapabilities.SupportedTopologies"/>; the dispatch
/// workflow refuses incompatible pairs at intake (AC9).</param>
/// <param name="Region">Provider-specific region identifier (free-form
/// string, see <see cref="ProviderCapabilities.Regions"/>). <c>null</c>
/// asks the provider to use its default.</param>
/// <param name="Tier">Resource sizing hint — <c>"starter"</c>,
/// <c>"pro"</c>, <c>"enterprise"</c>. Each provider maps the tier to its
/// own SKUs. <c>null</c> asks for the default tier.</param>
/// <param name="CustomName">Operator-supplied prefix for provider
/// resource names (e.g. <c>"acme-prod"</c>). <c>null</c> lets the provider
/// auto-generate a name.</param>
/// <param name="ExistingDatabaseUrl">For
/// <see cref="ProvisioningTopology.Managed"/> / BYO only — the operator
/// hands the platform a working database URL the provider must validate
/// (probe + version check). MUST be <c>null</c> for the other
/// topologies.</param>
/// <param name="ExistingEngineUrl">For
/// <see cref="ProvisioningTopology.Managed"/> / BYO only — the URL of an
/// engine the operator has already deployed.</param>
/// <param name="ExtraTags">Operator-supplied metadata attached to the
/// provisioning event audit trail. Keys/values are opaque strings; the
/// provider may forward selected tags to the underlying cloud API but is
/// NOT required to.</param>
public sealed record ProvisioningRequest(
    ProvisioningTopology Topology,
    string? Region = null,
    string? Tier = null,
    string? CustomName = null,
    string? ExistingDatabaseUrl = null,
    string? ExistingEngineUrl = null,
    IReadOnlyDictionary<string, string>? ExtraTags = null);
