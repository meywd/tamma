using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.GitLab;

/// <summary>
/// Story 31-6 — top-level driver facade. Composes
/// <see cref="GitLabPlatformClient"/> + <see cref="GitLabActionsClient"/>.
///
/// <para>Capability set is seeded from
/// <see cref="PlatformKindCapabilityMatrix.DefaultsFor"/> for
/// <see cref="PlatformKind.GitLab"/> then narrowed based on the detected
/// GitLab version (probed by <see cref="GitLabPlatformDriverFactory"/> via
/// <c>GET /version</c>): instances below
/// <see cref="MinimumPrLifecycleVersion"/> — or whose version probe failed —
/// drop <see cref="PlatformCapability.PrLifecycle"/> and the client answers
/// the six lifecycle verbs with the typed <c>capability_unsupported</c>
/// refusal without touching the network (pinned by the capability contract
/// test).</para>
/// </summary>
internal sealed class GitLabPlatformDriver : IGitPlatformDriver
{
    /// <summary>
    /// Epic 31 P6 M1 — floor for the six 31-13 PR lifecycle verbs. The
    /// binding constraint is <c>reviewer_ids</c> on the update-MR API:
    /// introduced in GitLab 13.8 (gitlab-org/gitlab!51186) but IGNORED by
    /// the update endpoint until the 13.9 fix (gitlab-org/gitlab#299846 /
    /// #320780, both closed Jan–Feb 2021), and only documented on update
    /// from 13.9 — so 13.9 is the honest floor. Everything else the family
    /// needs predates it: <c>state_event</c> (close/reopen),
    /// <c>labels</c>/<c>add_labels</c>/<c>remove_labels</c> (all present in
    /// the v13.8.0 update-MR doc), and the <c>Draft:</c> title prefix
    /// (13.2+; legacy <c>WIP:</c> read-compat kept — WIP write support was
    /// removed in 14.8, gitlab-org/gitlab!79693).
    /// </summary>
    public static readonly Version MinimumPrLifecycleVersion = new(13, 9);

    /// <summary>True when the detected version supports the PR lifecycle
    /// verb family. A null version (probe failed) is conservatively
    /// unsupported — the client then answers the typed
    /// <c>capability_unsupported</c> refusal without touching the network,
    /// per the capability contract.</summary>
    public static bool SupportsPrLifecycle(Version? detectedVersion) =>
        detectedVersion is not null && detectedVersion >= MinimumPrLifecycleVersion;

    public PlatformKind Kind => PlatformKind.GitLab;
    public IGitPlatformClient Client { get; }
    public IGitPlatformActionsClient? Actions { get; }

    /// <summary>Epic 31 P4 M4 — CI-secrets (variables) surface, mounted by
    /// the factory. Non-null on every factory-built GitLab driver.</summary>
    public ICiSecretsProvisioner? CiSecrets { get; }

    public IReadOnlySet<PlatformCapability> Capabilities { get; }

    /// <summary>Detected GitLab version — exposed for diagnostics + tests.</summary>
    public Version? DetectedVersion { get; }

    public GitLabPlatformDriver(
        IGitPlatformClient client,
        IGitPlatformActionsClient? actions,
        ICiSecretsProvisioner? ciSecrets = null,
        Version? detectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        Client = client;
        Actions = actions;
        CiSecrets = ciSecrets;
        DetectedVersion = detectedVersion;
        Capabilities = ComputeCapabilities(detectedVersion);
    }

    /// <summary>
    /// Test/factory hook — supply a custom capability set when the
    /// driver narrows the matrix defaults (e.g. read-only token drops
    /// Secrets).
    /// </summary>
    internal GitLabPlatformDriver(
        IGitPlatformClient client,
        IGitPlatformActionsClient? actions,
        IReadOnlySet<PlatformCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(capabilities);
        Client = client;
        Actions = actions;
        Capabilities = capabilities;
    }

    /// <summary>
    /// Compute the effective capability set for a detected GitLab version.
    /// Public to support testing without standing up the full factory.
    /// Mirrors <c>GiteaPlatformDriver.ComputeCapabilities</c>: a failed
    /// version probe conservatively drops <see cref="PlatformCapability.PrLifecycle"/>
    /// so the client answers typed <c>capability_unsupported</c> instead of
    /// guessing.
    /// </summary>
    public static IReadOnlySet<PlatformCapability> ComputeCapabilities(Version? detectedVersion)
    {
        var defaults = new HashSet<PlatformCapability>(
            PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitLab));
        if (!SupportsPrLifecycle(detectedVersion))
        {
            defaults.Remove(PlatformCapability.PrLifecycle);
        }
        return defaults;
    }
}
