using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.Gitea;

/// <summary>
/// Story 31-4 implementation of <see cref="IGitPlatformDriver"/>.
///
/// <para>Capability set is seeded from
/// <see cref="PlatformKindCapabilityMatrix.DefaultsFor"/> for
/// <see cref="PlatformKind.Gitea"/> then narrowed based on the
/// detected Gitea version: instances older than 1.21 don't have the
/// Actions API, so the driver removes
/// <see cref="PlatformCapability.Actions"/> +
/// <see cref="PlatformCapability.Artifacts"/> and returns null from
/// <see cref="Actions"/>.</para>
///
/// <para>Version detection runs in
/// <see cref="GiteaPlatformDriverFactory"/> so the driver is
/// pre-configured at construction — callers can read
/// <see cref="Capabilities"/> safely without I/O.</para>
/// </summary>
public sealed class GiteaPlatformDriver : IGitPlatformDriver
{
    /// <summary>
    /// Major.minor of the lowest Gitea version that ships the Actions
    /// API. Anything below this gets the read-only capability set.
    /// </summary>
    public static readonly Version MinimumActionsVersion = new(1, 21);

    public PlatformKind Kind => PlatformKind.Gitea;

    public IGitPlatformClient Client { get; }

    public IGitPlatformActionsClient? Actions { get; }

    /// <summary>Epic 31 P4 M4 — CI-secrets surface (Story 31-8), mounted by
    /// the factory when the detected version advertises Secrets (1.21+).</summary>
    public ICiSecretsProvisioner? CiSecrets { get; }

    public IReadOnlySet<PlatformCapability> Capabilities { get; }

    /// <summary>Detected Gitea version — exposed for diagnostics + tests.</summary>
    public Version? DetectedVersion { get; }

    internal GiteaPlatformDriver(
        IGitPlatformClient client,
        IGitPlatformActionsClient? actions,
        IReadOnlySet<PlatformCapability> capabilities,
        Version? detectedVersion,
        ICiSecretsProvisioner? ciSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(capabilities);
        Client = client;
        Actions = actions;
        Capabilities = capabilities;
        DetectedVersion = detectedVersion;
        CiSecrets = ciSecrets;
    }

    /// <summary>
    /// Compute the effective capability set for a given Gitea version.
    /// Public to support testing without standing up the full factory.
    /// </summary>
    public static IReadOnlySet<PlatformCapability> ComputeCapabilities(Version? detectedVersion)
    {
        var defaults = new HashSet<PlatformCapability>(
            PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.Gitea));
        if (detectedVersion is null || detectedVersion < MinimumActionsVersion)
        {
            defaults.Remove(PlatformCapability.Actions);
            defaults.Remove(PlatformCapability.Artifacts);
            // Gitea Secrets API ships alongside Actions in 1.21; older
            // instances don't have it.
            defaults.Remove(PlatformCapability.Secrets);
        }
        return defaults;
    }
}
