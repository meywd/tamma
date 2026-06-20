namespace Tamma.Api.Services.Providers;

/// <summary>
/// Reads a tenant's BYOK provider key plaintext from the Epic 29 secret
/// cabinet at runtime. The tenant-scoped sibling of
/// <c>RuntimeSecretResolver</c> (which is platform-scoped only): it queries
/// the cabinet for a <c>SecretScope.Tenant</c> row named by the provider and
/// reads its active version's plaintext via
/// <c>ISecretStoreBackend.GetVersionPlaintextAsync</c> — honouring the
/// "<c>ISecretStore</c> never surfaces plaintext" boundary.
///
/// <para>Pulled out behind an interface so the resolver's precedence / cache /
/// event / fail-closed algorithm is unit-testable without a Postgres
/// container; the real implementation
/// (<see cref="CabinetTenantProviderKeyReader"/>) is integration-tested
/// against the cabinet.</para>
/// </summary>
public interface ITenantProviderKeyReader
{
    /// <summary>
    /// Resolve a tenant BYOK key. Returns the plaintext + active version when a
    /// usable tenant-scoped row exists, or null when absent / scrubbed. NEVER
    /// throws on "absent" — a cabinet probe failure degrades to a null result
    /// (logged WARN) so the resolver proceeds to the platform fallback.
    /// </summary>
    Task<TenantProviderKey?> TryReadAsync(
        Guid tenantId, string cabinetName, CancellationToken ct = default);
}

/// <summary>A resolved BYOK key plaintext + the version it came from.</summary>
/// <param name="Plaintext">The raw key — request-scoped; never logged/emitted.</param>
/// <param name="VersionNumber">Active cabinet version number.</param>
public sealed record TenantProviderKey(string Plaintext, int VersionNumber);
