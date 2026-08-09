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

    /// <summary>
    /// Epic 31 P5 M1 — floor for the six 31-13 PR lifecycle verbs. The
    /// binding constraint is the review-request endpoint
    /// (<c>POST /repos/{o}/{r}/pulls/{n}/requested_reviewers</c>), present
    /// since Gitea 1.14 (verified against <c>routers/api/v1/api.go</c> on
    /// release/v1.14). Close/reopen (PATCH state), issue-side label
    /// add/remove by id, and the WIP-title draft toggle all predate it.
    /// Research note (2026-08-09): Gitea has NEVER shipped a <c>draft</c>
    /// field on Create/EditPullRequestOption (checked v1.19..v1.24 and
    /// main) — draft state is the WIP title prefix; the response-side
    /// <c>draft</c> boolean exists only since 1.22 (see
    /// <see cref="MinimumDraftFieldVersion"/>), so the client infers draft
    /// from the title prefix on older instances.
    /// </summary>
    public static readonly Version MinimumPrLifecycleVersion = new(1, 14);

    /// <summary>
    /// Lowest Gitea version whose PR API RESPONSES carry the computed
    /// <c>draft</c> boolean (added in 1.22; absent ≤1.21). Below this the
    /// client's title-prefix inference is the only draft signal.
    /// </summary>
    public static readonly Version MinimumDraftFieldVersion = new(1, 22);

    /// <summary>True when the detected version supports the PR lifecycle
    /// verb family. A null version (probe failed) is conservatively
    /// unsupported — the client then answers the typed
    /// <c>capability_unsupported</c> refusal without touching the network,
    /// per the capability contract.</summary>
    public static bool SupportsPrLifecycle(Version? detectedVersion) =>
        detectedVersion is not null && detectedVersion >= MinimumPrLifecycleVersion;

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
        // Epic 31 P5 M1 — lifecycle verbs need 1.14+ (requested_reviewers);
        // a failed version probe conservatively drops the flag so the client
        // answers typed capability_unsupported instead of guessing.
        if (!SupportsPrLifecycle(detectedVersion))
        {
            defaults.Remove(PlatformCapability.PrLifecycle);
        }
        return defaults;
    }
}
