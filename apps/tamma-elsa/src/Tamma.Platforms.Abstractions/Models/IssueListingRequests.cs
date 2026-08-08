namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Epic 31 P3 (seam 5) — input for
/// <see cref="IGitPlatformClient.ListIssuesAsync"/>: the work-item
/// selection read the autonomous loop's <c>/api/engine/issues</c>
/// callback rides on. Drivers exclude pull requests from the result
/// (platforms that model PRs as issues filter them out).
/// </summary>
/// <param name="Owner">Repo owner.</param>
/// <param name="RepoName">Repo name.</param>
/// <param name="State">"open" | "closed" | "all" (platform-native filter).</param>
/// <param name="Labels">Label names the issues must ALL carry; null/empty = no filter.</param>
/// <param name="PerPage">Page size (drivers clamp to platform limits).</param>
/// <param name="Page">1-based page.</param>
public sealed record ListIssuesRequest(
    string Owner,
    string RepoName,
    string State = "open",
    IReadOnlyList<string>? Labels = null,
    int PerPage = 30,
    int Page = 1);

/// <summary>
/// Epic 31 P3 (seam 5) — input for
/// <see cref="IGitPlatformClient.CreateIssueAsync"/> (the triage /
/// issue-creation flow's engine callback). Drivers that cannot apply
/// labels/assignees at creation time apply them best-effort and never
/// fail the create for it.
/// </summary>
public sealed record CreateIssueRequest(
    string Owner,
    string RepoName,
    string Title,
    string? Body = null,
    IReadOnlyList<string>? Labels = null,
    IReadOnlyList<string>? Assignees = null);

/// <summary>
/// Epic 31 P3 (seam 5) — result of
/// <see cref="IGitPlatformClient.ListSecurityAlertsAsync"/>. Alert
/// payloads are platform-native raw JSON texts (one per alert) — the
/// abstraction does not normalize security-alert schemas across
/// platforms; callers project what they need. Platforms without a
/// security-alert surface answer the typed
/// <c>capability_unsupported</c> refusal instead.
/// </summary>
/// <param name="DependabotJson">Dependency-vulnerability alerts (raw JSON per alert).</param>
/// <param name="CodeScanningJson">Static-analysis alerts (raw JSON per alert).</param>
public sealed record SecurityAlerts(
    IReadOnlyList<string> DependabotJson,
    IReadOnlyList<string> CodeScanningJson);
