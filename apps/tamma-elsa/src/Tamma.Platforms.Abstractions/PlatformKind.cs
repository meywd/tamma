namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-1 AC4 — concrete git hosting platforms Tamma can drive.
///
/// <para>Used as the keyed-DI key when registering an
/// <see cref="IGitPlatformDriver"/>. Adding a value here is the first
/// step of adding a new driver; the actual driver implementation
/// lives in a sibling project (see <c>Tamma.Platforms.GitHub</c> in
/// 31-3, <c>Tamma.Platforms.Gitea</c> in 31-4, etc.).</para>
///
/// <para>Bitbucket and AzureDevOps are reserved values — drivers
/// land in 31-11 / 31-12. The capability matrix in
/// <see cref="PlatformKindCapabilityMatrix"/> already encodes their
/// expected support so the onboarding picker can render them as
/// "coming soon" without breaking when they ship.</para>
/// </summary>
public enum PlatformKind
{
    GitHub = 1,
    Gitea = 2,
    Forgejo = 3,
    GitLab = 4,
    Bitbucket = 5,
    AzureDevOps = 6,
}
