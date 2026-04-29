namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Platform-neutral issue record. We don't surface assignee or
/// milestone in 31-1 — adding them later doesn't break callers.
/// </summary>
public sealed record Issue(
    string Number,
    string Title,
    string? Body,
    IssueState State,
    string HtmlUrl,
    IReadOnlyList<string> Labels);

public enum IssueState
{
    Open = 1,
    Closed = 2,
}
