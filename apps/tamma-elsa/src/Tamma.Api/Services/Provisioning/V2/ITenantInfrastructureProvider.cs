namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// v2 of the per-tenant provisioning surface. Replaces v1's Cranl-only
/// <c>ITenantProvisioner</c> with a backend-pluggable contract: any
/// number of providers (Cranl, Hetzner Cloud, Cloudflare Workers + D1,
/// BYO, the dev null seam) implement this interface and the dispatch
/// workflow (Story 30-2) selects between them by <see cref="ProviderKey"/>
/// via <see cref="TenantProviderRegistry"/>.
/// </summary>
/// <remarks>
/// <para><b>Operating modes (CLAUDE.md §"Operating Modes")</b>:</para>
/// <list type="bullet">
///   <item><description><b>single-user</b> — only the
///     <see cref="NullTenantProvider"/> is wired; tenants don't get
///     provisioned dynamically (the sole user runs on the central / shared
///     Postgres). Calling <see cref="ProvisionAsync"/> on the null seam
///     throws <see cref="NotSupportedException"/> by design (Story 30-7's
///     onboarding UI is SaaS-only).</description></item>
///   <item><description><b>SaaS</b> — one or more real providers are
///     registered keyed by <see cref="ProviderKey"/>; the registry
///     resolves the provider for a tenant from the
///     <c>tenants.provider_key</c> column (lands in Story 30-3) or from
///     the dispatch workflow's request (Story 30-2). Tenant principals
///     never call provisioning directly — only platform-owner-scoped
///     admin endpoints do, gated by the <c>OwnerAccess</c> policy.</description></item>
/// </list>
///
/// <para><b>Idempotency</b>: every method is idempotent.
/// <see cref="ProvisionAsync"/> called twice with the same
/// <paramref name="request"/> on the same <paramref name="tenantId"/>
/// returns the current snapshot rather than starting parallel work; this
/// is the contract the Elsa workflow retry semantics depend on.
/// <see cref="DeprovisionAsync"/> on a tenant that's already
/// deprovisioned is a no-op. Implementations that mint cloud resources
/// MUST check for existing identifiers before calling create-APIs.</para>
///
/// <para><b>Multi-tenancy of the provider itself</b>: providers are
/// platform-scoped, not tenant-scoped. One <c>CranlTenantProvider</c>
/// instance serves every Cranl-backed tenant. Tenants cannot bring their
/// own provider implementation — providers are wired by the platform
/// operator at startup.</para>
/// </remarks>
public interface ITenantInfrastructureProvider
{
    /// <summary>Stable lookup key for
    /// <see cref="TenantProviderRegistry"/>. Convention: lowercase
    /// snake_case, no spaces. Reserved keys: <c>"null"</c>, <c>"cranl"</c>,
    /// <c>"hetzner"</c>, <c>"cloudflare"</c>, <c>"byo"</c>.</summary>
    string ProviderKey { get; }

    /// <summary>What this provider can do — drives onboarding-UI filters
    /// and dispatch-workflow gating. Cheap to call; implementations may
    /// cache the result.</summary>
    ProviderCapabilities GetCapabilities();

    /// <summary>
    /// Begin provisioning infrastructure for the tenant. Returns
    /// immediately with a <see cref="ProvisioningResult"/> snapshot — the
    /// long-running cloud-API walk runs out-of-band on the dispatch
    /// workflow's queue.
    ///
    /// <para><b>Failure mode for unsupported topology</b>: when
    /// <paramref name="request"/>.Topology is not in
    /// <see cref="ProviderCapabilities.SupportedTopologies"/>, return a
    /// <see cref="ProvisioningResult"/> whose
    /// <see cref="ProvisioningStatusSnapshot.State"/> is
    /// <see cref="ProvisioningState.Failed"/> and whose
    /// <see cref="ProvisioningStatusSnapshot.FailureReason"/> is
    /// <c>"unsupported_topology"</c>. Do NOT throw — the dispatch
    /// workflow expects a structured failure (AC9).</para>
    /// </summary>
    Task<ProvisioningResult> ProvisionAsync(
        Guid tenantId,
        ProvisioningRequest request,
        CancellationToken ct);

    /// <summary>Read the current provisioning snapshot for the tenant.
    /// Pure read; doesn't call cloud APIs unless the implementation
    /// chooses to refresh on every call (Cranl will not — it polls on
    /// a separate timer).</summary>
    Task<ProvisioningStatusSnapshot> GetStatusAsync(
        Guid tenantId,
        CancellationToken ct);

    /// <summary>Tear down everything the provider created for the tenant.
    /// Implementations MUST be idempotent; calling on a tenant with no
    /// provider resources is a no-op that returns successfully. Honour
    /// <see cref="DeprovisioningRequest.CleanupMode"/>.</summary>
    Task DeprovisionAsync(
        Guid tenantId,
        DeprovisioningRequest request,
        CancellationToken ct);

    /// <summary>Resolve the runtime endpoints (DB URL, engine URL, ...)
    /// for a provisioned tenant. The per-request routing layer (Story
    /// 30-8) and the engine-dispatch layer call this. Implementations
    /// SHOULD cache the result and invalidate on state transitions.
    /// Throws <see cref="InvalidOperationException"/> when the tenant is
    /// not in a state where endpoints are available
    /// (e.g. <see cref="ProvisioningState.Pending"/>).</summary>
    Task<TenantEndpoints> ResolveEndpointsAsync(
        Guid tenantId,
        CancellationToken ct);
}
