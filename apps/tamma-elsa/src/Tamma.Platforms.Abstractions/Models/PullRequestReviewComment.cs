namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Epic 31 P1 (stage 1) — one file/line-anchored review comment as
/// returned by
/// <see cref="IGitPlatformClient.ListPullRequestReviewCommentsAsync"/>.
/// Distinct from <see cref="IssueComment"/> because review comments
/// carry an anchor (<see cref="Path"/>/<see cref="Line"/>) the loop's
/// review-analysis step keys on; both may be null for comments whose
/// anchor was outdated by a force-push.
/// </summary>
public sealed record PullRequestReviewComment(
    string Id,
    string Body,
    string AuthorLogin,
    DateTimeOffset CreatedAt,
    string? Path,
    int? Line);
