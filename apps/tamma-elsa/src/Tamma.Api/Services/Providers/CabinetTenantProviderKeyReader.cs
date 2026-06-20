using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Cabinet-backed <see cref="ITenantProviderKeyReader"/> (Story 32-3 AC5).
/// The tenant-scoped sibling of
/// <c>Tamma.Api.Services.Secrets.Stopgap.RuntimeSecretResolver.TryReadCabinetAsync</c>:
/// queries <see cref="SecretsDbContext"/> for a <c>Scope == "tenant"</c> row
/// with the caller's <c>TenantId</c> and the provider's cabinet name, then
/// reads the active version's plaintext via
/// <see cref="ISecretStoreBackend.GetVersionPlaintextAsync"/>.
///
/// <para>Tenant isolation (AC11): the EF predicate pins
/// <c>TenantId == tenantId</c>, so a BYOK row belonging to another tenant can
/// never be returned for this caller.</para>
///
/// <para>Cabinet probe failures degrade to a null result (logged WARN) so the
/// resolver treats them as "BYOK absent" and proceeds to the platform
/// fallback — never leaking the failure as a 500 and never substituting a
/// wrong key.</para>
/// </summary>
public sealed class CabinetTenantProviderKeyReader : ITenantProviderKeyReader
{
    private readonly IDbContextFactory<SecretsDbContext> _secretsFactory;
    private readonly ISecretStoreBackend _backend;
    private readonly ILogger<CabinetTenantProviderKeyReader> _logger;

    public CabinetTenantProviderKeyReader(
        IDbContextFactory<SecretsDbContext> secretsFactory,
        ISecretStoreBackend backend,
        ILogger<CabinetTenantProviderKeyReader> logger)
    {
        ArgumentNullException.ThrowIfNull(secretsFactory);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(logger);
        _secretsFactory = secretsFactory;
        _backend = backend;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TenantProviderKey?> TryReadAsync(
        Guid tenantId, string cabinetName, CancellationToken ct = default)
    {
        try
        {
            await using var ctx = await _secretsFactory
                .CreateDbContextAsync(ct).ConfigureAwait(false);

            var row = await ctx.Secrets
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    s => s.Name == cabinetName
                         && s.Scope == "tenant"
                         && s.TenantId == tenantId,
                    ct)
                .ConfigureAwait(false);

            if (row is null || row.ActiveVersionNumber <= 0)
            {
                return null;
            }

            var plaintext = await _backend
                .GetVersionPlaintextAsync(row.Id, row.ActiveVersionNumber, ct)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(plaintext))
            {
                return null;
            }

            return new TenantProviderKey(plaintext, row.ActiveVersionNumber);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Degrade-to-absent — never leak the probe failure or any bytes.
            _logger.LogWarning(ex,
                "BYOK cabinet probe for tenant {TenantId} secret {CabinetName} " +
                "threw; treating as BYOK-absent and falling through to platform " +
                "fallback.", tenantId, cabinetName);
            return null;
        }
    }
}
