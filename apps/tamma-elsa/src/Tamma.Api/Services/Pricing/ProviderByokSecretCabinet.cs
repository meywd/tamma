using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Default <see cref="IProviderByokSecretCabinet"/>. Write → the merged
/// <see cref="ISecretStore"/> facade (<c>CreateAsync</c> mints v1 active + emits the
/// <c>SECRET.WRITE</c> cabinet audit); remove → the cabinet's own
/// <see cref="SecretsDbContext"/> + <see cref="ISecretStoreBackend"/> seam (the facade
/// has no row-delete). Mirrors <see cref="Integrations.IntegrationCredentialCabinet"/>
/// but pins the provider-BYOK slug (<c>provider/&lt;name&gt;/api-key</c>) via
/// <see cref="ProviderCabinetNames.Byok"/> so the key it writes is byte-identical to
/// the one Story 32-3's resolver reads.
/// </summary>
public sealed class ProviderByokSecretCabinet : IProviderByokSecretCabinet
{
    private readonly ISecretStore _secretStore;
    private readonly IDbContextFactory<SecretsDbContext> _secretsFactory;
    private readonly ISecretStoreBackend _backend;
    private readonly ILogger<ProviderByokSecretCabinet> _logger;

    public ProviderByokSecretCabinet(
        ISecretStore secretStore,
        IDbContextFactory<SecretsDbContext> secretsFactory,
        ISecretStoreBackend backend,
        ILogger<ProviderByokSecretCabinet> logger)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _secretsFactory = secretsFactory ?? throw new ArgumentNullException(nameof(secretsFactory));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SecretMetadata> WriteAsync(
        Guid tenantId,
        string providerCanonical,
        string apiKey,
        Guid ownerUserId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerCanonical);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var cabinetName = ProviderCabinetNames.Byok(providerCanonical);

        // Idempotent re-enable (AC12): a prior key for this (tenant, provider) is
        // removed first so CreateAsync mints a FRESH v1-active — the tenant's newly
        // supplied key becomes the active version immediately (the facade's RotateAsync
        // leaves the successor PENDING, which would NOT swap the active key). NEVER logs
        // the value.
        await RemoveAsync(tenantId, providerCanonical, ct).ConfigureAwait(false);

        // CreateAsync mints v1 active + emits SECRET.WRITE; it is atomic (compensates a
        // partial create) so a bad key surfaces as a throw with NO partial write.
        return await _secretStore.CreateAsync(
            new CreateSecretRequest(
                Name: cabinetName,
                Scope: SecretScope.Tenant,
                TenantId: tenantId,
                Purpose: SecretPurpose.ApiKey,
                ConsumerRefs: new[] { new ConsumerRef(providerCanonical, "api-key") },
                OwnerUserId: ownerUserId,
                RotationSchedule: RotationSchedule.None,
                InitialPlaintext: apiKey),
            ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        Guid tenantId, string providerCanonical, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerCanonical);

        var cabinetName = ProviderCabinetNames.Byok(providerCanonical);

        await using var ctx = await _secretsFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var row = await ctx.Secrets
            .FirstOrDefaultAsync(
                s => s.Name == cabinetName
                     && s.Scope == "tenant"
                     && s.TenantId == tenantId,
                ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }

        var versions = await ctx.SecretVersions
            .Where(v => v.SecretId == row.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Scrub the ciphertext bytes out of the backend first (best-effort; idempotent
        // — a KeyNotFound means the bytes were never stored / already gone). NEVER logs
        // the bytes.
        foreach (var v in versions)
        {
            try
            {
                await _backend.DeleteVersionAsync(row.Id, v.VersionNumber, ct).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                // never stored
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Best-effort backend scrub failed removing BYOK key {CabinetName} v{Version} "
                    + "for tenant {TenantId}.", cabinetName, v.VersionNumber, tenantId);
            }
        }

        if (versions.Count > 0)
        {
            ctx.SecretVersions.RemoveRange(versions);
        }
        ctx.Secrets.Remove(row);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Retired BYOK key {CabinetName} for tenant {TenantId}.", cabinetName, tenantId);
        return true;
    }
}
