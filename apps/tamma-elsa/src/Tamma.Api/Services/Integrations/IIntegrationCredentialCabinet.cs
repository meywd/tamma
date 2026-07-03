using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Services.Integrations;

/// <summary>
/// The governed write surface for a per-tenant integration credential bundle in
/// the Epic 29 secret cabinet. Backs the BYOK write endpoints
/// (<c>IntegrationCredentialEndpoints</c>).
///
/// <para><b>Set</b> goes through the merged <see cref="ISecretStore"/> facade
/// (<c>CreateAsync</c>) — the governed / audited write API that mints v1 active
/// and emits the <c>SECRET.WRITE</c> cabinet audit. <b>Remove</b> deletes the
/// secret row + version rows and scrubs the backend bytes through the cabinet's
/// own seam, because the facade exposes no secret-row deletion and its
/// <c>RetireVersionAsync</c> refuses the active version — so a working "drop the
/// credential" (which a later set can cleanly re-create) is not expressible on
/// the facade. NON-migration: both paths reuse the existing
/// <c>secrets</c>/<c>secret_versions</c> tables.</para>
/// </summary>
public interface IIntegrationCredentialCabinet
{
    /// <summary>
    /// Create the tenant-scoped credential secret <paramref name="cabinetName"/>
    /// with <paramref name="bundleJson"/> as its first active version. Throws
    /// <see cref="InvalidOperationException"/> when one already exists (set-once;
    /// the caller maps that to 409 — remove then re-set to change it).
    /// </summary>
    Task<SecretMetadata> SetAsync(
        Guid tenantId,
        string cabinetName,
        string consumerSystem,
        string bundleJson,
        Guid ownerUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Remove the tenant-scoped credential secret so resolution falls back to the
    /// system tier / fails loud, and a later set can re-create the same slug.
    /// Returns <c>true</c> when a row existed and was removed, <c>false</c> when
    /// nothing matched.
    /// </summary>
    Task<bool> RemoveAsync(
        Guid tenantId, string cabinetName, CancellationToken ct = default);
}
