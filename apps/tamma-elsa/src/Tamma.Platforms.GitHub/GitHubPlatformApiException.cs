using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Epic 31 P1 stage 2 — thrown by
/// <see cref="GitHubPlatformClient.ListAccessibleReposAsync"/> when
/// the platform rejects the listing (bad token, network down). The
/// no-throw contract applies to the <see cref="PlatformResult{T}"/>
/// verbs; the accessible-repos enumeration has no result envelope, and
/// silently yielding nothing is exactly the vacuous-probe bug this
/// stage fixes (a junk credential used to persist a
/// <c>connected</c> installation row because the old stub
/// yield-broke). The onboarding probe
/// (<c>PlatformConnectService</c>) catches this and reports
/// <c>auth_probe_failed</c>.
/// </summary>
public sealed class GitHubPlatformApiException : Exception
{
    /// <summary>The typed platform error the listing hit.</summary>
    public PlatformError Error { get; }

    public GitHubPlatformApiException(string message, PlatformError error)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }
}
