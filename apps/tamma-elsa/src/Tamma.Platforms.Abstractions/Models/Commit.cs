namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Epic 31 P1 (stage 1) — platform-neutral commit summary returned by
/// <see cref="IGitPlatformClient.ListCommitsAsync"/>. Deliberately
/// shallow (no file list, no stats) — it mirrors what the loop's
/// commit reads actually consume today
/// (<c>GitHubIntegrationService.GetGitHubCommitsAsync</c>); per-file
/// detail comes from
/// <see cref="IGitPlatformClient.ListBranchFileChangesAsync"/>.
/// </summary>
/// <param name="Sha">Full commit SHA.</param>
/// <param name="Message">Full commit message.</param>
/// <param name="AuthorName">Git author name (not the platform login).</param>
/// <param name="Timestamp">Author timestamp.</param>
public sealed record Commit(
    string Sha,
    string Message,
    string AuthorName,
    DateTimeOffset Timestamp);
