using Tamma.Api.Services.Provisioning.V2;

namespace Tamma.Api.Services.Provisioning.V2.Cranl;

/// <summary>
/// Capability constants for the Cranl-backed
/// <see cref="CranlTenantProviderV2"/>. Kept on a separate type so the
/// onboarding UI (Story 30-7) and the dispatch workflow (Story 30-2) can
/// reference the same source of truth without instantiating the provider.
///
/// <para>Cranl ships:</para>
/// <list type="bullet">
///   <item><description><b>Topologies</b>:
///     <see cref="ProvisioningTopology.DatabaseOnly"/> (db-only tier where
///     the Tamma engine continues to run on shared infra) and
///     <see cref="ProvisioningTopology.DedicatedCompute"/> (the canonical
///     "Cranl project + db + Elsa app" shape — today's full provisioning
///     flow). Cranl does NOT support
///     <see cref="ProvisioningTopology.Managed"/> (BYO is its own provider
///     in Story 30-6).</description></item>
///   <item><description><b>Features</b>:
///     <see cref="ProviderFeatures.DedicatedDb"/> (every Cranl-provisioned
///     database is fully isolated; no shared schema). Custom domains,
///     autoscale, and managed backups are NOT yet wired through this
///     provider — they're roadmap items behind their own stories.</description></item>
///   <item><description><b>Regions</b>: from
///     <c>docs/vendors/cranl/README.md</c> — <c>germany-1</c>,
///     <c>us-east-1</c>, <c>saudi-arabia-1</c>, <c>egypt-1</c>,
///     <c>india-1</c>. The default the platform picks when the operator
///     does not specify is sourced from <c>Cranl:DefaultRegion</c>
///     configuration; the static list here is the MENU of valid choices,
///     not the default.</description></item>
/// </list>
/// </summary>
public static class CranlCapabilities
{
    /// <summary>Stable lookup key for the registry. Convention:
    /// lowercase snake_case.</summary>
    public const string ProviderKey = "cranl";

    /// <summary>Human-readable name shown in the onboarding UI.</summary>
    public const string DisplayName = "Cranl (per-tenant project + db + engine)";

    /// <summary>Bit-flagged topologies Cranl can fulfil. Composed from
    /// <see cref="ProvisioningTopology.DatabaseOnly"/> (db-only tier where
    /// the engine stays shared) and
    /// <see cref="ProvisioningTopology.DedicatedCompute"/> (full per-tenant
    /// project + db + Elsa app stack — today's flow).</summary>
    public const ProvisioningTopology SupportedTopologies =
        ProvisioningTopology.DatabaseOnly | ProvisioningTopology.DedicatedCompute;

    /// <summary>Feature set Cranl exposes today. <see cref="ProviderFeatures.DedicatedDb"/>
    /// is set because every provisioned database is a fully isolated Cranl db
    /// instance (no shared schema). Other features (custom domains, autoscale,
    /// managed backups) are deliberately off because they aren't wired through
    /// the v1 provisioner Cranl flow yet.</summary>
    public const ProviderFeatures Features = ProviderFeatures.DedicatedDb;

    /// <summary>Region menu mirrored from <c>docs/vendors/cranl/README.md</c>.
    /// The dispatch workflow validates the operator's choice against this
    /// list when a region is supplied; <c>null</c> falls through to
    /// <c>Cranl:DefaultRegion</c>.</summary>
    public static readonly IReadOnlyList<string> Regions = new[]
    {
        "germany-1",
        "us-east-1",
        "saudi-arabia-1",
        "egypt-1",
        "india-1",
    };
}
