using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;

namespace Tamma.Api.Services.Integrations;

/// <summary>
/// Default <see cref="IIntegrationCredentialCabinet"/>. Set → the merged
/// <see cref="ISecretStore"/> facade; remove → the cabinet's own
/// <see cref="SecretsDbContext"/> + <see cref="ISecretStoreBackend"/> seam (the
/// facade has no row-delete). See the interface doc for the rationale.
/// </summary>
public sealed class IntegrationCredentialCabinet : IIntegrationCredentialCabinet
{
    private readonly ISecretStore _secretStore;
    private readonly IDbContextFactory<SecretsDbContext> _secretsFactory;
    private readonly ISecretStoreBackend _backend;
    private readonly ILogger<IntegrationCredentialCabinet> _logger;

    public IntegrationCredentialCabinet(
        ISecretStore secretStore,
        IDbContextFactory<SecretsDbContext> secretsFactory,
        ISecretStoreBackend backend,
        ILogger<IntegrationCredentialCabinet> logger)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _secretsFactory = secretsFactory ?? throw new ArgumentNullException(nameof(secretsFactory));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SecretMetadata> SetAsync(
        Guid tenantId,
        string cabinetName,
        string consumerSystem,
        string bundleJson,
        Guid ownerUserId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cabinetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleJson);

        // The whole bundle is one tenant-scoped ApiKey secret; CreateAsync mints
        // v1 active and emits SECRET.WRITE. A duplicate throws InvalidOperationException.
        return await _secretStore.CreateAsync(
            new CreateSecretRequest(
                Name: cabinetName,
                Scope: SecretScope.Tenant,
                TenantId: tenantId,
                Purpose: SecretPurpose.ApiKey,
                ConsumerRefs: new[] { new ConsumerRef(consumerSystem, "config") },
                OwnerUserId: ownerUserId,
                RotationSchedule: RotationSchedule.None,
                InitialPlaintext: bundleJson),
            ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        Guid tenantId, string cabinetName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cabinetName);

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

        // Scrub the ciphertext bytes out of the backend first (best-effort;
        // idempotent — a KeyNotFound means the bytes were never stored / already
        // gone). NEVER logs the bytes.
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
                    "Best-effort backend scrub failed removing integration credential {CabinetName}" +
                    " v{Version} for tenant {TenantId}.", cabinetName, v.VersionNumber, tenantId);
            }
        }

        if (versions.Count > 0)
        {
            ctx.SecretVersions.RemoveRange(versions);
        }
        ctx.Secrets.Remove(row);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Removed integration credential {CabinetName} for tenant {TenantId}.",
            cabinetName, tenantId);
        return true;
    }
}
