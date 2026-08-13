using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.Gitea;

/// <summary>
/// Epic 31 P5 M1 — thrown by
/// <see cref="GiteaPlatformClient.ListAccessibleReposAsync"/> when the
/// platform rejects the listing (bad token, network down). The no-throw
/// contract applies to the <see cref="PlatformResult{T}"/> verbs; the
/// accessible-repos enumeration has no result envelope, and silently
/// yield-breaking on a failure is the vacuous-probe class the GitHub
/// driver's P1 fix closed (a junk credential would enumerate "empty",
/// pass the onboarding probe, and persist a <c>connected</c> row). The
/// onboarding probe (<c>PlatformConnectService</c>) catches this and
/// reports <c>auth_probe_failed</c>.
/// </summary>
public sealed class GiteaPlatformApiException : Exception
{
    /// <summary>The typed platform error the listing hit.</summary>
    public PlatformError Error { get; }

    public GiteaPlatformApiException(string message, PlatformError error)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }
}
