using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Services.Platforms;

/// <summary>
/// Story 31-2 — production
/// <see cref="IPlatformCredentialReader"/>. Reads installation
/// credential plaintext through the same Story 29-2 seam every other
/// credentialled subsystem uses
/// (compare <c>DefaultAlertChannelSecretReader</c>):
///
/// <list type="number">
///   <item>Look up the secret row by
///         <c>(scope, tenantId?, name)</c> in
///         <see cref="SecretsDbContext"/>.</item>
///   <item>Read the active version's plaintext via
///         <see cref="ISecretStoreBackend.GetVersionPlaintextAsync"/>.</item>
/// </list>
///
/// <para>This adapter ships in <c>Tamma.Api</c> (not
/// <c>Tamma.Platforms</c>) because <c>SecretsDbContext</c> +
/// <c>ISecretStoreBackend</c> live in <c>Tamma.Api</c>; the resolver
/// consumes the slim
/// <see cref="IPlatformCredentialReader"/> port so the platform layer
/// has no compile-time dependency on the API project. The "no
/// bypass" rule is enforced because every secret read in this class
/// goes through Story 29's interfaces.</para>
/// </summary>
public sealed class SecretStorePlatformCredentialReader
    : IPlatformCredentialReader
{
    private readonly IDbContextFactory<SecretsDbContext> _secretsFactory;
    private readonly ISecretStoreBackend _backend;

    public SecretStorePlatformCredentialReader(
        IDbContextFactory<SecretsDbContext> secretsFactory,
        ISecretStoreBackend backend)
    {
        ArgumentNullException.ThrowIfNull(secretsFactory);
        ArgumentNullException.ThrowIfNull(backend);
        _secretsFactory = secretsFactory;
        _backend = backend;
    }

    /// <inheritdoc />
    public async Task<string?> ReadActivePlaintextAsync(
        string scope,
        Guid? tenantId,
        string name,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Validate scope/tenantId invariant up front so a bad
        // installation row surfaces a clean argument error rather
        // than a missing-row null at the bottom.
        if (scope == "tenant" && tenantId is null)
        {
            throw new ArgumentException(
                "Tenant-scoped secret reads require a non-null tenantId.",
                nameof(tenantId));
        }
        if (scope == "platform" && tenantId is not null)
        {
            throw new ArgumentException(
                "Platform-scoped secrets must not carry a tenantId.",
                nameof(tenantId));
        }
        if (scope is not ("platform" or "tenant"))
        {
            throw new ArgumentException(
                $"Unknown secret scope '{scope}'. Expected 'platform' or 'tenant'.",
                nameof(scope));
        }

        await using var ctx = await _secretsFactory
            .CreateDbContextAsync(ct)
            .ConfigureAwait(false);

        var row = await ctx.Secrets
            .AsNoTracking()
            .FirstOrDefaultAsync(s =>
                s.Scope == scope
                && s.TenantId == tenantId
                && s.Name == name,
                ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }
        if (row.ActiveVersionNumber <= 0)
        {
            return null;
        }

        try
        {
            return await _backend
                .GetVersionPlaintextAsync(row.Id, row.ActiveVersionNumber, ct)
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }
}
