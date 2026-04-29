namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// The runtime addresses the platform needs to route a tenant request to
/// its engine + database. Returned by
/// <see cref="ITenantInfrastructureProvider.ResolveEndpointsAsync"/> and
/// embedded in <see cref="ProvisioningResult.Endpoints"/>.
/// </summary>
/// <param name="DatabaseUrl">Full Postgres connection URL for the tenant's
/// database (e.g. <c>postgresql://user:pass@host:5432/db</c>). Always set
/// for <see cref="ProvisioningTopology.DatabaseOnly"/> /
/// <see cref="ProvisioningTopology.DedicatedCompute"/>; provider-supplied
/// for <see cref="ProvisioningTopology.Managed"/>.
/// <para><b>Storage rule</b>: never persisted in plaintext. The provisioner
/// hands this back encrypted-at-rest is the caller's job (see
/// <c>TenantSecretProtector</c>) — the value crosses an in-process trust
/// boundary only.</para></param>
/// <param name="EngineHost">DNS hostname of the tenant's engine
/// (e.g. <c>tamma-engine-abc.cranl.net</c>). <c>null</c> when the engine
/// runs on shared platform infrastructure (i.e.
/// <see cref="ProvisioningTopology.DatabaseOnly"/>).</param>
/// <param name="EngineUrl">Fully-qualified base URL the platform uses to
/// dispatch workflow requests to the engine. <c>null</c> for the shared-
/// engine topology, same rationale as <paramref name="EngineHost"/>.</param>
/// <param name="CustomDomain">Operator-attached vanity domain
/// (e.g. <c>engine.acme.example</c>). <c>null</c> unless the provider
/// supports <see cref="ProviderFeatures.CustomDomains"/> and the operator
/// has wired one in.</param>
public sealed record TenantEndpoints(
    string DatabaseUrl,
    string? EngineHost,
    string? EngineUrl,
    string? CustomDomain = null);
