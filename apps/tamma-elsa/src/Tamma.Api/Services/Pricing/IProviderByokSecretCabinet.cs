using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-3 — the governed write surface for a tenant's BYOK provider API key in
/// the Epic 29 secret cabinet, backing the pricing BYOK toggle endpoints
/// (<c>PricingEndpoints.EnableByok</c> / <c>DisableByok</c>). The sibling of Story
/// 32-3's <c>ProviderCredentialEndpoints</c> secret write and Story integration-BYOK's
/// <see cref="Integrations.IIntegrationCredentialCabinet"/> — same cabinet, same slug.
///
/// <para>The key is stored under the cabinet slug
/// <c>provider/&lt;providerKey&gt;/api-key</c> (scope <c>Tenant</c>, purpose
/// <c>ApiKey</c>) — the SAME name Story 32-3's <c>IProviderCredentialResolver</c> reads
/// at LLM-call time, so the key written here on enable is the key 32-3 later resolves.
/// <paramref name="providerCanonical"/> MUST already be the RAW provider IDENTITY
/// (<c>ProviderIdentity.Normalize</c> — <c>Trim().ToLowerInvariant()</c>, NO alias-family
/// reduction) so the resolver's read for that same handle matches
/// (<c>gemini</c> stays <c>gemini</c>, never the rate-card family <c>google</c>).</para>
///
/// <para><b>Write</b> is idempotent (remove-then-create) so a re-enable rotates the
/// key to a new v1-active immediately (matching the one-active-row owner invariant).
/// <b>Remove</b> deletes the secret row + version rows and scrubs the backend bytes —
/// the merged <see cref="ISecretStore"/> facade exposes no row-delete and its
/// <c>RetireVersionAsync</c> refuses the active version, so a clean "drop the key"
/// (that a later enable can re-create) is expressed here, exactly like
/// <see cref="Integrations.IIntegrationCredentialCabinet"/>. NON-migration: both paths
/// reuse the existing <c>secrets</c> / <c>secret_versions</c> tables.</para>
/// </summary>
public interface IProviderByokSecretCabinet
{
    /// <summary>
    /// Store <paramref name="apiKey"/> as the tenant's active BYOK key for
    /// <paramref name="providerCanonical"/>. Idempotent: an existing key for the same
    /// <c>(tenant, provider)</c> is replaced (removed then re-created) so a re-enable
    /// rotates the value to a fresh v1-active. Returns the persisted metadata (the
    /// active version) — never the key.
    /// </summary>
    Task<SecretMetadata> WriteAsync(
        Guid tenantId,
        string providerCanonical,
        string apiKey,
        Guid ownerUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Retire the tenant's BYOK key for <paramref name="providerCanonical"/> so the
    /// next 32-3 resolve falls back to the platform leg and a later enable can
    /// re-create the same slug. Returns <c>true</c> when a row existed and was removed,
    /// <c>false</c> when nothing matched (idempotent disable).
    /// </summary>
    Task<bool> RemoveAsync(
        Guid tenantId, string providerCanonical, CancellationToken ct = default);
}
