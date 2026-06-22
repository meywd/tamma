namespace Tamma.Api.Services.Security;

/// <summary>
/// Story 32-4 — the minimal read seam for the SaaS auth / entitlement check
/// (the 403 <see cref="ProviderGateOutcome.TenantNotEntitled"/> path). The gate
/// owns ONLY the typed surfacing of the entitlement result, never the
/// entitlement RULES — those are owned by Epic 34's SaaS auth/entitlement
/// engine. When that engine exposes a richer surface, this seam is backed by it
/// (a DI swap); the gate contract is unchanged.
///
/// <para>The shipped default (<see cref="PermissiveTenantProviderEntitlement"/>)
/// returns <c>true</c> for every tenant × provider — so an <c>api-key</c>
/// provider in SaaS is allowed by default. The 403 path only fires once a real
/// entitlement engine is wired in place of the permissive default.</para>
/// </summary>
public interface ITenantProviderEntitlement
{
    /// <summary>
    /// Is the tenant entitled to use the managed-LLM path for
    /// <paramref name="providerName"/>? Evaluated only for <c>api-key</c>
    /// providers in SaaS (after the auth-model classification, before allow).
    /// A <c>false</c> result surfaces as a 403
    /// <see cref="ProviderGateOutcome.TenantNotEntitled"/>.
    /// </summary>
    Task<bool> IsTenantEntitledAsync(Guid? tenantId, string providerName, CancellationToken ct = default);
}

/// <summary>
/// Story 32-4 — the shipped default entitlement seam: permissive (every tenant
/// is entitled to every api-key provider). This keeps "api-key + entitled" the
/// default SaaS behaviour while the typed 403 outcome surface is in place,
/// ready for Epic 34's entitlement engine to replace it (DI swap, no contract
/// change). It performs no I/O and reads no secret.
/// </summary>
public sealed class PermissiveTenantProviderEntitlement : ITenantProviderEntitlement
{
    public Task<bool> IsTenantEntitledAsync(
        Guid? tenantId, string providerName, CancellationToken ct = default) =>
        Task.FromResult(true);
}
