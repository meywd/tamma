namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Comment on an issue or PR (platforms treat issue comments and PR
/// review-thread comments uniformly enough for one shape — file/line
/// review comments are distinct, see
/// <see cref="IGitPlatformClient.CreatePullRequestReviewCommentAsync"/>).
/// </summary>
public sealed record IssueComment(
    string Id,
    string Body,
    string AuthorLogin,
    DateTimeOffset CreatedAt);
