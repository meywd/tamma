namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Platform-neutral repository record. Maps cleanly to GitHub repos,
/// GitLab projects, Gitea / Forgejo repos, Bitbucket repos, Azure
/// DevOps repositories.
/// </summary>
/// <param name="Host">
/// Hostname only — <c>github.com</c>, <c>gitlab.com</c>, or a
/// self-hosted host (<c>git.acme.corp</c>). No scheme, no path.
/// </param>
/// <param name="Owner">
/// Owner of the repo: GitHub user/org login, GitLab group/user path
/// (URL-encoded as the platform stores it), Bitbucket workspace, etc.
/// </param>
/// <param name="Name">Short repo name (no slashes).</param>
/// <param name="DefaultBranch">e.g. <c>main</c> / <c>master</c> / <c>trunk</c>.</param>
/// <param name="IsPrivate">True if the repo is private to the owner.</param>
/// <param name="Description">Optional repo description; may be null.</param>
/// <param name="CloneUrl">HTTPS clone URL (drivers normalize SSH → HTTPS).</param>
/// <param name="HtmlUrl">Browser-facing URL for the repo's main page.</param>
public sealed record Repo(
    string Host,
    string Owner,
    string Name,
    string DefaultBranch,
    bool IsPrivate,
    string? Description,
    string CloneUrl,
    string HtmlUrl);
