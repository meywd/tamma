namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Story 30-8 — small abstraction the
/// <see cref="V2TenantEndpointDirectory"/> uses to read a tenant's
/// <c>provider_key</c> from the control plane. Decouples the directory
/// from EF / SQL specifics so unit tests can substitute a deterministic
/// in-memory lookup, and so the production implementation can choose
/// between EF shadow-property and raw-SQL strategies as Story 30-3
/// rolls out the physical column.
///
/// <para>Returns <c>null</c> when the tenant has no <c>provider_key</c>
/// set (legacy tenants, or tenants on the shared-infra path) — the
/// directory translates this to
/// <c>TenantEndpointResolution.NotApplicable</c> so the LRU resolver
/// falls back to the pre-30-8 <c>EncryptedConnectionString</c> path.</para>
///
/// <para>Throws <c>TenantNotFoundException</c> when the tenant id is
/// unknown to the control plane — bubbles verbatim through the
/// directory.</para>
/// </summary>
public interface ITenantProviderKeyLookup
{
    Task<string?> GetProviderKeyAsync(Guid tenantId, CancellationToken ct);
}
