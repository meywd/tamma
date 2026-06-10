using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Story 31-3 — top-level <see cref="IGitPlatformDriver"/> facade for
/// GitHub. Composes <see cref="GitHubPlatformClient"/> (source-host
/// surface) + <see cref="GitHubActionsPlatformClient"/> (CI surface).
///
/// <para>The capability set comes from the static
/// <see cref="PlatformKindCapabilityMatrix.DefaultsFor"/> for
/// <see cref="PlatformKind.GitHub"/> — which already advertises
/// <see cref="PlatformCapability.LibsodiumSecrets"/> +
/// <see cref="PlatformCapability.WebhookHmac"/> +
/// <see cref="PlatformCapability.PerAppInstallationAuth"/>. The
/// driver MAY narrow the matrix in the future when constructed for a
/// PAT-only install (no GitHub App) — that branch is left to a
/// follow-up story; today's driver advertises the full default set.</para>
///
/// <para>Why "wrap, don't rewrite": Story 31-3 makes GitHub a peer to
/// Gitea / GitLab / Forgejo behind <see cref="IGitPlatformDriver"/>
/// without touching the existing Octokit clients in
/// <c>Tamma.Api</c> / <c>Tamma.Activities</c>. Future stories will
/// flesh out the source-host operations
/// (<see cref="IGitPlatformClient.OpenPullRequestAsync"/> etc.) by
/// extending the inner <c>IGitHubActionsClient</c> seam — at which
/// point this driver picks them up automatically.</para>
/// </summary>
public sealed class GitHubPlatformDriver : IGitPlatformDriver
{
    /// <inheritdoc />
    public PlatformKind Kind => PlatformKind.GitHub;

    /// <inheritdoc />
    public IGitPlatformClient Client { get; }

    /// <inheritdoc />
    public IGitPlatformActionsClient? Actions { get; }

    /// <inheritdoc />
    public IReadOnlySet<PlatformCapability> Capabilities { get; }

    /// <summary>
    /// Build a driver from explicit collaborators. Consumed by
    /// <see cref="GitHubPlatformDriverFactory"/> — production code goes
    /// through the factory + DI rather than constructing the driver
    /// directly. Tests use the constructor to inject mocks.
    /// </summary>
    public GitHubPlatformDriver(
        IGitPlatformClient client,
        IGitPlatformActionsClient? actions)
        : this(client, actions, PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitHub))
    {
    }

    /// <summary>
    /// Construct with a custom capability set — used by future
    /// PAT-only / read-only paths that narrow the matrix defaults.
    /// </summary>
    public GitHubPlatformDriver(
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
}
