namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// What a provider declares it can do. The onboarding UI (Story 30-7)
/// reads this matrix to render the (backend, topology) picker; the
/// dispatch workflow (Story 30-2) reads it to refuse incompatible
/// requests at intake time rather than mid-saga.
/// </summary>
/// <param name="ProviderKey">Stable lookup key — e.g. <c>"cranl"</c>,
/// <c>"hetzner"</c>, <c>"cloudflare"</c>, <c>"byo"</c>, <c>"null"</c>.
/// Same value as <see cref="ITenantInfrastructureProvider.ProviderKey"/>.</param>
/// <param name="DisplayName">Human-readable name for the onboarding UI.</param>
/// <param name="SupportedTopologies">Bit-flags of every topology the
/// provider can fulfil. A provider that advertises a topology MUST be
/// able to satisfy a <see cref="ITenantInfrastructureProvider.ProvisionAsync"/>
/// request using it.</param>
/// <param name="Regions">Free-form region identifiers the provider exposes
/// (e.g. <c>germany-1</c>, <c>us-east-1</c>, <c>auto</c>). Strings, not
/// an enum, so each provider can mint its own naming without forcing a
/// platform-wide vocabulary.</param>
/// <param name="Features">Optional add-on capabilities (custom domains,
/// dedicated DB, backups, autoscale).</param>
/// <param name="MaxTenantsPerOrg">Soft cap the provider enforces per
/// owning organisation. <c>null</c> means uncapped from the provider's
/// side (the platform may still impose a cap independently).</param>
/// <param name="CostHint">Coarse cost/quota signal for the cost dashboard
/// (Story 30-10). <c>null</c> when the provider doesn't publish a hint.
/// The structure deliberately stays simple — Story 30-10 will replace
/// it with a richer record without churning this interface.</param>
public sealed record ProviderCapabilities(
    string ProviderKey,
    string DisplayName,
    ProvisioningTopology SupportedTopologies,
    IReadOnlyList<string> Regions,
    ProviderFeatures Features = ProviderFeatures.None,
    int? MaxTenantsPerOrg = null,
    ProviderCostHint? CostHint = null)
{
    /// <summary>"Provider is registered but supports nothing" — used by
    /// the null provider in single-user mode and as a base for tests.</summary>
    public static ProviderCapabilities None(string providerKey, string displayName) =>
        new(providerKey, displayName, ProvisioningTopology.None, Array.Empty<string>());

    /// <summary>Predicate for the dispatch workflow: did the caller pick a
    /// topology the provider supports?</summary>
    public bool SupportsTopology(ProvisioningTopology topology) =>
        topology != ProvisioningTopology.None
        && (SupportedTopologies & topology) == topology;
}

/// <summary>Coarse cost hint a provider publishes for dashboards.
/// Story 30-10 may replace this with a richer schema; keep callers off
/// the fields that aren't on the brief.</summary>
/// <param name="UnitsPerMonth">Abstract "cost units" (price-list-relative)
/// for one tenant on the smallest tier the provider sells. Zero when the
/// provider is free / shared (e.g. the null provider).</param>
/// <param name="Currency">ISO-4217 code, defaults to <c>USD</c>. Free-form
/// because some providers (Cranl, Hetzner) bill in EUR.</param>
public sealed record ProviderCostHint(decimal UnitsPerMonth, string Currency = "USD");
