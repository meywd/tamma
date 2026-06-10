namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Platform-neutral PR / MR record.
///
/// <para>Per impl-plan open question: GitHub has independent state
/// (open/closed/merged) AND draft flag. GitLab MRs treat WIP/Draft as
/// a flag on title prefix. We surface BOTH so callers can ask
/// "is this PR open?" and "is this PR draft?" independently.</para>
/// </summary>
/// <param name="Number">
/// Stringified PR/MR number — kept as string so platforms with
/// non-integer ids (Bitbucket uses uuids in some surfaces) are not
/// boxed out.
/// </param>
/// <param name="Title">PR title.</param>
/// <param name="Body">PR description body, possibly null.</param>
/// <param name="SourceBranch">Source / head branch (the branch with new commits).</param>
/// <param name="TargetBranch">Target / base branch (the branch being merged into).</param>
/// <param name="State">Open / Closed / Merged.</param>
/// <param name="IsDraft">True if the PR is in draft mode.</param>
/// <param name="HtmlUrl">Browser-facing URL.</param>
/// <param name="AuthorLogin">Login of the PR author.</param>
/// <param name="CreatedAt">Creation timestamp.</param>
/// <param name="UpdatedAt">Last-update timestamp.</param>
public sealed record PullRequest(
    string Number,
    string Title,
    string? Body,
    string SourceBranch,
    string TargetBranch,
    PullRequestState State,
    bool IsDraft,
    string HtmlUrl,
    string AuthorLogin,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Lifecycle state. Drivers MUST normalize platform-specific values
/// (e.g. GitLab "merged"/"opened"/"closed", GitHub
/// "open"/"closed"+merged-flag) into one of these values.
/// </summary>
public enum PullRequestState
{
    Open = 1,
    Closed = 2,
    Merged = 3,
}
