namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Epic 31 P1 (stage 1) — input for
/// <see cref="IGitPlatformClient.AddIssueLabelsAsync"/>. The multi-value
/// verb gets a record (the <see cref="AddPullRequestLabelsRequest"/>
/// convention); close and single-label removal take positional scalars
/// on the interface.
/// </summary>
public sealed record AddIssueLabelsRequest(
    string Owner,
    string RepoName,
    string IssueNumber,
    IReadOnlyList<string> Labels);
