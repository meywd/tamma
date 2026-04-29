using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-2 — per-kind factory the resolver consumes to mint a
/// driver instance bound to a specific
/// <see cref="PlatformInstallation"/>. Each driver project (31-3
/// GitHub, 31-4 Gitea, 31-6 GitLab, etc.) ships one factory and
/// registers it via keyed DI with the same
/// <see cref="PlatformKind"/> key its driver registers under.
///
/// <para>The factory shape (vs. a plain
/// <c>AddKeyedSingleton&lt;IGitPlatformDriver&gt;</c>) exists because
/// each tenant's driver needs a different
/// <see cref="PlatformInstallation.BaseUrl"/> + credential. The
/// factory takes both as inputs and returns a fully-wired driver
/// instance; the resolver caches the result per
/// <c>(tenantId, kind)</c> so the factory is invoked at most once per
/// cache window.</para>
///
/// <para>Registration convention:</para>
/// <code>
/// services.AddKeyedSingleton&lt;IGitPlatformDriverFactory, GitHubDriverFactory&gt;(
///     PlatformKind.GitHub);
/// </code>
///
/// <para>The factory MUST NOT cache driver instances internally; the
/// resolver owns the cache lifecycle. The factory MAY cache underlying
/// HTTP handlers / token clients keyed by
/// <see cref="PlatformInstallation.Id"/> — those are decoupled from
/// the driver and survive cache evictions.</para>
/// </summary>
public interface IGitPlatformDriverFactory
{
    /// <summary>
    /// The platform kind this factory mints drivers for. Must match
    /// the keyed-DI key the factory is registered under; the resolver
    /// asserts this on first use.
    /// </summary>
    PlatformKind Kind { get; }

    /// <summary>
    /// Build a driver instance bound to the given installation. The
    /// driver consumes <paramref name="credentialPlaintext"/> exactly
    /// once at construction and stores whatever derived form it needs
    /// (e.g. the JWT for GitHub Apps, the bearer token for GitLab);
    /// the plaintext bytes are scrubbed by the caller after this call
    /// returns.
    /// </summary>
    /// <param name="installation">Per-tenant installation context —
    /// tenant id, kind, base URL, external id.</param>
    /// <param name="credentialPlaintext">UTF-8 plaintext credential
    /// fetched via Story 29's
    /// <c>ISecretStore</c> + <c>ISecretStoreBackend</c> seam.
    /// Implementers MUST NOT log or persist this value.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IGitPlatformDriver> CreateAsync(
        PlatformInstallation installation,
        string credentialPlaintext,
        CancellationToken ct = default);
}
