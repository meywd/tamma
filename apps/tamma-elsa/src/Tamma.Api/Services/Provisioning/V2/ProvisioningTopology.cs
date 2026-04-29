namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// What kind of infrastructure a tenant gets, independent of which backend
/// operates it. The topology is orthogonal to the provider (Cranl, Hetzner,
/// Cloudflare, BYO) so the onboarding UI can filter on (backend, topology)
/// pairs from the capability matrix.
/// </summary>
/// <remarks>
/// Story 30-1 — Epic 30 foundation. The values are <c>[Flags]</c> so a
/// provider can declare more than one supported topology in
/// <see cref="ProviderCapabilities.SupportedTopologies"/>.
/// </remarks>
[Flags]
public enum ProvisioningTopology
{
    /// <summary>No topology — used as the empty bit-flag default. Never a
    /// valid value for <see cref="ProvisioningRequest.Topology"/>.</summary>
    None = 0,

    /// <summary>Provision only a database; the Tamma engine continues to run
    /// on shared platform infrastructure. Cheapest tier.</summary>
    DatabaseOnly = 1 << 0,

    /// <summary>Provision a dedicated compute host (VM / Worker / container)
    /// + a dedicated database + the Tamma engine. Today's Cranl shape.</summary>
    DedicatedCompute = 1 << 1,

    /// <summary>Tenant owns the infrastructure end-to-end; the platform only
    /// registers the endpoint URLs and routes traffic. Used for BYO
    /// (bring-your-own) enterprise tenants.</summary>
    Managed = 1 << 2
}
