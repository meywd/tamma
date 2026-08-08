using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Epic 31 P1 stage 2 — top-level <see cref="IGitPlatformDriver"/>
/// facade for GitHub. Composes <see cref="GitHubPlatformClient"/>
/// (source-host surface, REST + GraphQL) +
/// <see cref="GitHubActionsPlatformClient"/> (CI surface) — both now
/// REAL implementations over <see cref="GitHubHttpClient"/>, absorbed
/// from the former Tamma.Api live path per the execution plan §2
/// decision ("the GitHub driver ABSORBS the live client").
///
/// <para>The capability set comes from
/// <see cref="ComputeCapabilities"/> — the static matrix defaults,
/// narrowed to drop
/// <see cref="PlatformCapability.PerAppInstallationAuth"/> when the
/// driver was built in PAT mode (a PAT install is not per-App
/// installation auth; advertising it would be the kind of lie the
/// capability contract test exists to catch).</para>
/// </summary>
public sealed class GitHubPlatformDriver : IGitPlatformDriver
{
    /// <inheritdoc />
    public PlatformKind Kind => PlatformKind.GitHub;

    /// <inheritdoc />
    public IGitPlatformClient Client { get; }

    /// <inheritdoc />
    public IGitPlatformActionsClient? Actions { get; }

    /// <summary>Epic 31 P4 M4 — Story 31-8's CI-secrets surface, mounted by
    /// the factory when the capability set advertises Secrets. Non-null on
    /// every factory-built GitHub driver.</summary>
    public ICiSecretsProvisioner? CiSecrets { get; }

    /// <inheritdoc />
    public IReadOnlySet<PlatformCapability> Capabilities { get; }

    /// <summary>
    /// Build a driver from explicit collaborators with the full matrix
    /// default capability set. Production goes through
    /// <see cref="GitHubPlatformDriverFactory"/>; tests may construct
    /// directly.
    /// </summary>
    public GitHubPlatformDriver(
        IGitPlatformClient client,
        IGitPlatformActionsClient? actions)
        : this(client, actions, PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitHub))
    {
    }

    /// <summary>
    /// Construct with an explicit capability set (the factory passes
    /// <see cref="ComputeCapabilities"/>'s result).
    /// </summary>
    public GitHubPlatformDriver(
        IGitPlatformClient client,
        IGitPlatformActionsClient? actions,
        IReadOnlySet<PlatformCapability> capabilities,
        ICiSecretsProvisioner? ciSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(capabilities);
        Client = client;
        Actions = actions;
        Capabilities = capabilities;
        CiSecrets = ciSecrets;
    }

    /// <summary>
    /// Compute the driver's live capability set for the given
    /// credential mode: matrix defaults for App mode; PAT mode drops
    /// <see cref="PlatformCapability.PerAppInstallationAuth"/>.
    /// </summary>
    public static IReadOnlySet<PlatformCapability> ComputeCapabilities(GitHubAuth auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        var set = new HashSet<PlatformCapability>(
            PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitHub));
        if (auth is GitHubAuth.Pat)
        {
            set.Remove(PlatformCapability.PerAppInstallationAuth);
        }
        return set;
    }
}
