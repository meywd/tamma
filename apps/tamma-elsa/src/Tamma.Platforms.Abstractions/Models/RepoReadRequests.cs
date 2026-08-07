namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Epic 31 P1 (stage 1) — input for
/// <see cref="IGitPlatformClient.ListCommitsAsync"/>.
/// </summary>
/// <param name="Owner">Repo owner (org/user/group path).</param>
/// <param name="RepoName">Repo name.</param>
/// <param name="Ref">Branch name or SHA to list commits from.</param>
/// <param name="Since">Only commits authored after this instant, when
/// set (mirrors the live path's <c>since</c> filter).</param>
public sealed record ListCommitsRequest(
    string Owner,
    string RepoName,
    string Ref,
    DateTimeOffset? Since = null);

/// <summary>
/// Epic 31 P1 (stage 1) — input for
/// <see cref="IGitPlatformClient.ListBranchFileChangesAsync"/>: the
/// files changed on <see cref="Branch"/> relative to
/// <see cref="BaseRef"/> (the repo's default branch when null —
/// matching the live path's main-then-master compare).
/// </summary>
public sealed record ListBranchFileChangesRequest(
    string Owner,
    string RepoName,
    string Branch,
    string? BaseRef = null);
