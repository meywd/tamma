namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// File diff entry within a PR/MR — the shape returned by
/// <see cref="IGitPlatformClient.ListPullRequestFilesAsync"/>.
/// </summary>
/// <param name="Path">Repo-relative path of the changed file.</param>
/// <param name="Status">Lifecycle status of this file in the PR.</param>
/// <param name="Additions">Lines added.</param>
/// <param name="Deletions">Lines removed.</param>
public sealed record PrFile(
    string Path,
    PrFileStatus Status,
    int Additions,
    int Deletions);

/// <summary>
/// Normalized file-status shape. Drivers map their platform values
/// into one of these — GitHub uses
/// added/modified/removed/renamed/copied/changed/unchanged; GitLab
/// uses new_path / renamed_file / new_file / deleted_file booleans.
/// </summary>
public enum PrFileStatus
{
    Added = 1,
    Modified = 2,
    Removed = 3,
    Renamed = 4,
    Copied = 5,
    /// <summary>
    /// Catch-all for platform-specific values that don't map to the
    /// above (GitHub's "changed"/"unchanged"). Caller can fall back
    /// to additions/deletions counts.
    /// </summary>
    Other = 99,
}
