namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Epic 31 P1 (stage 1) — input for
/// <see cref="IGitPlatformClient.CreateReleaseAsync"/>. Shape mirrors
/// the live GitHub path (<c>GitHubIntegrationService.CreateGitHubReleaseAsync</c>)
/// this verb absorbs in P1 stage 2: tag is required; name defaults to
/// the tag; target commitish is only sent when supplied (platforms
/// default it to the repo's default branch and ignore it when the tag
/// already exists).
/// </summary>
public sealed record CreateReleaseRequest(
    string Owner,
    string RepoName,
    string TagName,
    string? Name = null,
    string? Body = null,
    string? TargetCommitish = null,
    bool Draft = false,
    bool Prerelease = false);

/// <summary>
/// Platform-neutral release record returned by
/// <see cref="IGitPlatformClient.CreateReleaseAsync"/>.
/// </summary>
/// <param name="Id">Platform-assigned release id (stringified — GitHub
/// uses a numeric id, GitLab keys releases by tag).</param>
/// <param name="TagName">The git tag the release points at.</param>
/// <param name="Name">Human-readable release title.</param>
/// <param name="HtmlUrl">Browser URL of the release page.</param>
/// <param name="Draft">True when the release is an unpublished draft.</param>
/// <param name="Prerelease">True when marked as a pre-release.</param>
public sealed record Release(
    string Id,
    string TagName,
    string Name,
    string HtmlUrl,
    bool Draft,
    bool Prerelease);
