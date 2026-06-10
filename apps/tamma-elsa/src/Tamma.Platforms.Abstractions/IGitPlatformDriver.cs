namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-1 AC5 — top-level facade every driver registers under.
/// Composes <see cref="IGitPlatformClient"/> (mandatory) +
/// <see cref="IGitPlatformActionsClient"/> (optional) + the
/// driver's effective capability set.
///
/// <para>Registration convention (consumed by Story 31-2):</para>
/// <code>
/// services.AddKeyedSingleton&lt;IGitPlatformDriver, GitHubDriver&gt;(
///     PlatformKind.GitHub);
/// </code>
///
/// <para>Resolution:</para>
/// <code>
/// var driver = serviceProvider.GetRequiredKeyedService&lt;IGitPlatformDriver&gt;(
///     PlatformKind.GitHub);
/// if (driver.Actions is null) ... // platform has no CI surface
/// if (!driver.Capabilities.Contains(PlatformCapability.LibsodiumSecrets))
///     ... // can't ship sealed-box secrets
/// </code>
///
/// <para><see cref="Capabilities"/> is the *effective* capability set
/// for this driver instance — usually
/// <see cref="PlatformKindCapabilityMatrix.DefaultsFor"/> for
/// <see cref="Kind"/>, but a driver MAY narrow it based on actual
/// config (e.g. GitHub driver removes
/// <see cref="PlatformCapability.PerAppInstallationAuth"/> when
/// running with a personal-access token instead of a GitHub App).</para>
/// </summary>
public interface IGitPlatformDriver
{
    /// <summary>The platform this driver targets.</summary>
    PlatformKind Kind { get; }

    /// <summary>Source-host surface — always non-null.</summary>
    IGitPlatformClient Client { get; }

    /// <summary>
    /// CI surface — null when the driver doesn't implement CI
    /// dispatch (pure git, read-only mode). Equivalent to
    /// <c>!Capabilities.Contains(PlatformCapability.Actions)</c>;
    /// callers should check <see cref="Capabilities"/> for
    /// programmatic decisions and use this property only when
    /// they're already reaching for a CI call.
    /// </summary>
    IGitPlatformActionsClient? Actions { get; }

    /// <summary>
    /// Story 31-8 — CI secrets provisioner. Null when the driver
    /// doesn't implement secret push (pure git, or a platform that
    /// has no programmable secrets API). Equivalent to
    /// <c>!Capabilities.Contains(PlatformCapability.Secrets)</c>;
    /// callers should check <see cref="Capabilities"/> programmatically
    /// and reach for this property only when they're invoking a
    /// secrets call.
    /// </summary>
    ICiSecretsProvisioner? CiSecrets => null;

    /// <summary>
    /// Effective capabilities for THIS driver instance. Mode
    /// behavior:
    /// <list type="bullet">
    ///   <item>single-user mode: one driver per
    ///         <see cref="Kind"/>, configured for the lone user.</item>
    ///   <item>SaaS mode: the registry creates one driver per
    ///         (tenantId, kind) pair; capabilities depend on the
    ///         tenant's installation (an app-installation tenant
    ///         gets <see cref="PlatformCapability.PerAppInstallationAuth"/>;
    ///         a PAT-based tenant doesn't).</item>
    /// </list>
    /// </summary>
    IReadOnlySet<PlatformCapability> Capabilities { get; }
}
