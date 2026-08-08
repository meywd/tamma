using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.Gitea;

/// <summary>
/// Story 31-5 — Forgejo compat shim. Forgejo branched from Gitea at
/// v1.18 (Dec 2022) and intentionally retains REST + DB compatibility
/// with Gitea; the only divergences relevant to this driver are:
///
/// <list type="bullet">
///   <item>The <c>/api/v1/version</c> response uses the build-suffix
///         shape <c>1.21.5+forgejo-3</c> (parsed identically by the
///         Gitea factory's '+'/'-' suffix-strip).</item>
///   <item>Outbound webhooks default to header
///         <c>X-Forgejo-Signature</c>; older forks still emit
///         <c>X-Gitea-Signature</c>. Both are accepted via
///         <see cref="GiteaWebhookSignatureVerifier"/>'s configurable
///         header-name list.</item>
/// </list>
///
/// <para>Composition over inheritance: this driver wraps a fully-built
/// <see cref="GiteaPlatformDriver"/> rather than subclassing it. If
/// Forgejo diverges in a way the wrapper can't paper over (e.g. a
/// rename on a hot endpoint), promote this class to a full driver with
/// its own <see cref="GiteaHttpClient"/> subclass — until then, the
/// shim is cheaper than duplication. See README "Forgejo
/// compatibility" section.</para>
///
/// <para><see cref="Kind"/> returns <see cref="PlatformKind.Forgejo"/>
/// so the onboarding picker can brand Forgejo separately and
/// <see cref="PlatformKindCapabilityMatrix"/> can diverge in future
/// without touching this class.</para>
/// </summary>
public sealed class ForgejoPlatformDriver : IGitPlatformDriver
{
    private readonly GiteaPlatformDriver _inner;

    /// <summary>
    /// Build a Forgejo driver that delegates to a pre-constructed
    /// Gitea driver. The wrapped driver MUST have been built against
    /// a Forgejo base URL — the factory enforces this.
    /// </summary>
    internal ForgejoPlatformDriver(GiteaPlatformDriver inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public PlatformKind Kind => PlatformKind.Forgejo;

    /// <inheritdoc />
    public IGitPlatformClient Client => _inner.Client;

    /// <inheritdoc />
    public IGitPlatformActionsClient? Actions => _inner.Actions;

    /// <summary>Epic 31 P4 M4 — delegates the CI-secrets surface to the
    /// wrapped Gitea driver (Forgejo keeps the Gitea secrets API).</summary>
    public ICiSecretsProvisioner? CiSecrets => _inner.CiSecrets;

    /// <inheritdoc />
    /// <remarks>
    /// Today: identical to <see cref="GiteaPlatformDriver.Capabilities"/>
    /// for the same detected version (Forgejo v15 ~ Gitea v1.22). If
    /// Forgejo adds a capability Gitea doesn't (e.g. native OIDC), the
    /// wrapper can override here without touching the Gitea code path.
    /// </remarks>
    public IReadOnlySet<PlatformCapability> Capabilities => ComputeCapabilities(_inner.DetectedVersion);

    /// <summary>Detected Forgejo (or compat-Gitea) version — diagnostics.</summary>
    public Version? DetectedVersion => _inner.DetectedVersion;

    /// <summary>
    /// Compute the effective capability set for a given Forgejo
    /// version. Today this delegates to the Gitea matrix because
    /// Forgejo retains identical API surface for the capabilities
    /// Tamma cares about. Kept distinct so divergence is a one-line
    /// edit, not a refactor.
    /// </summary>
    public static IReadOnlySet<PlatformCapability> ComputeCapabilities(Version? detectedVersion)
    {
        // Forgejo's Actions ship in the same release window as Gitea's
        // (Forgejo 1.21 inherits Gitea 1.21's Actions). The matrix
        // entry for Forgejo in PlatformKindCapabilityMatrix is also
        // identical to Gitea's, so seed from Forgejo's row to keep
        // narrowing logic ours, not Gitea's.
        var defaults = new HashSet<PlatformCapability>(
            PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.Forgejo));
        if (detectedVersion is null || detectedVersion < GiteaPlatformDriver.MinimumActionsVersion)
        {
            defaults.Remove(PlatformCapability.Actions);
            defaults.Remove(PlatformCapability.Artifacts);
            defaults.Remove(PlatformCapability.Secrets);
        }
        return defaults;
    }
}
