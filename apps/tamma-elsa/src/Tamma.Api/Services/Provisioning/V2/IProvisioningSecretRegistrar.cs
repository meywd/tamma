using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Story 30-3 — the RegisterSecrets saga step (Step 6 of
/// <see cref="ProvisionTenantV2Workflow"/>). Registers the per-tenant
/// secrets a freshly-provisioned tenant genuinely needs into the Epic 29
/// <see cref="ISecretStore"/> cabinet, and knows how to retire them on
/// rollback.
///
/// <para><b>What it registers (and deliberately does NOT)</b>:</para>
/// <list type="bullet">
///   <item><description><b>Registered</b> — the per-tenant HMAC /
///     <c>TAMMA_SHARED_SECRET</c> shadow (<c>tenant:cranl/app-env-hmac</c>),
///     and ONLY for <see cref="ProvisioningTopology.DedicatedCompute"/>
///     (a per-tenant engine that signs its control-plane calls). See
///     <see cref="ProvisioningSecretRegistrar"/>.</description></item>
///   <item><description><b>NOT registered</b> — the
///     <c>tenant:db/cranl-connection</c> secret named in the story brief
///     (AC9). It is VESTIGIAL post-Epic-30 Phase B: DB routing no longer
///     flows through a stored per-tenant <c>DATABASE_URL</c> — every
///     tenant routes via the unified pool's encrypted connection-string
///     envelope (<c>CranlTenantProviderV2.TryBuildEndpoints</c> returns
///     <c>DatabaseUrl = string.Empty</c> and reads only the engine host).
///     Registering it would create a secret no consumer reads. See the
///     code comment in <see cref="ProvisioningSecretRegistrar"/>.</description></item>
/// </list>
///
/// <para><b>Guarded no-op</b>: for non-dedicated topologies
/// (<see cref="ProvisioningTopology.DatabaseOnly"/> /
/// <see cref="ProvisioningTopology.Managed"/>) there is no per-tenant
/// engine, so nothing is registered and
/// <see cref="RegisterInitialSecretsAsync"/> returns an empty list — a
/// DELIBERATE guarded no-op, not a stub.</para>
///
/// <para><b>Dormancy</b>: the dedicated-compute path is exercised only by
/// the opt-in Cranl backend with a dedicated per-tenant engine, which is
/// dormant in the default deployment (Cranl opt-in;
/// <c>PlatformTaskWorker:RunOnStartup=false</c>). This surface is
/// therefore unit-testable but not exercised end-to-end today.</para>
/// </summary>
public interface IProvisioningSecretRegistrar
{
    /// <summary>
    /// Register the per-tenant secrets the saga needs for
    /// <paramref name="topology"/>. Returns the refs that were registered
    /// (empty for the guarded no-op paths) so the caller can retire them
    /// on rollback via <see cref="RetireInitialSecretsAsync"/>.
    /// </summary>
    /// <remarks>
    /// FAIL LOUD: throws when a secret genuinely needs to be registered but
    /// the cabinet is unavailable or the underlying create fails — the saga
    /// must NOT proceed with a missing per-tenant secret. The
    /// "nothing to register" path is NOT a failure; it returns empty.
    /// </remarks>
    Task<IReadOnlyList<SecretRef>> RegisterInitialSecretsAsync(
        Guid tenantId, ProvisioningTopology topology, CancellationToken ct = default);

    /// <summary>
    /// Compensation for <see cref="RegisterInitialSecretsAsync"/>: retire
    /// each ref it registered. Idempotent and non-throwing — safe to run on
    /// rollback even when a secret was never created (e.g. registration
    /// threw before the create landed) or was already scrubbed.
    /// </summary>
    Task RetireInitialSecretsAsync(
        IReadOnlyList<SecretRef> registered, CancellationToken ct = default);
}
