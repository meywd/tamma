namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-2 — slim port that the platform resolver uses to fetch
/// installation credentials. The only production implementation
/// adapts Story 29's
/// <c>ISecretStore</c> + <c>ISecretStoreBackend</c> pair so EVERY
/// secret read in the resolver path goes through Epic 29's interface
/// — no bypass.
///
/// <para>This interface lives in <c>Tamma.Platforms.Abstractions</c>
/// rather than directly consuming <c>ISecretStore</c> (in
/// <c>Tamma.Api.Services.Secrets</c>) for two reasons:</para>
/// <list type="number">
///   <item>The platform abstraction must not depend on
///         <c>Tamma.Api</c> — that would force a circular reference
///         once <c>Tamma.Api</c> registers the resolver.</item>
///   <item>The plaintext-resolution flow is a two-call sequence on
///         the secret store (look up metadata via <c>SecretRef</c>,
///         then read plaintext via the backend with
///         <c>(secretId, versionNumber)</c>). Wrapping that in one
///         port keeps the resolver trivially testable with a Moq.</item>
/// </list>
/// </summary>
public interface IPlatformCredentialReader
{
    /// <summary>
    /// Read the active-version plaintext for an installation
    /// credential addressed by
    /// <paramref name="scope"/> + <paramref name="tenantId"/> +
    /// <paramref name="name"/>. Returns null when the secret does not
    /// exist, has no minted version yet, or has been scrubbed.
    /// </summary>
    /// <param name="scope">
    /// <c>"platform"</c> for installation-level credentials shared
    /// across tenants (rare — typically only Tamma's own GitHub App
    /// private key); <c>"tenant"</c> for per-tenant installation
    /// tokens.
    /// </param>
    /// <param name="tenantId">
    /// Owning tenant when <paramref name="scope"/> is <c>tenant</c>;
    /// null on platform scope.
    /// </param>
    /// <param name="name">Lower-kebab-case secret slug.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> ReadActivePlaintextAsync(
        string scope,
        Guid? tenantId,
        string name,
        CancellationToken ct = default);
}
