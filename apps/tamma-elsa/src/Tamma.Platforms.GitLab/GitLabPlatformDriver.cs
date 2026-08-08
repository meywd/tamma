using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.GitLab;

/// <summary>
/// Story 31-6 — top-level driver facade. Composes
/// <see cref="GitLabPlatformClient"/> + <see cref="GitLabActionsClient"/>.
/// Capability set comes from <see cref="PlatformKindCapabilityMatrix"/>
/// — the GitLab row already includes Actions, Artifacts, Secrets,
/// MaskedVariables, ProtectedVariables, PrFileReview,
/// WebhookStaticToken, ListAccessibleRepos.
/// </summary>
internal sealed class GitLabPlatformDriver : IGitPlatformDriver
{
    public PlatformKind Kind => PlatformKind.GitLab;
    public IGitPlatformClient Client { get; }
    public IGitPlatformActionsClient? Actions { get; }

    /// <summary>Epic 31 P4 M4 — CI-secrets (variables) surface, mounted by
    /// the factory. Non-null on every factory-built GitLab driver.</summary>
    public ICiSecretsProvisioner? CiSecrets { get; }

    public IReadOnlySet<PlatformCapability> Capabilities { get; }

    public GitLabPlatformDriver(
        IGitPlatformClient client,
        IGitPlatformActionsClient? actions,
        ICiSecretsProvisioner? ciSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        Client = client;
        Actions = actions;
        CiSecrets = ciSecrets;
        Capabilities = PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitLab);
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
}
