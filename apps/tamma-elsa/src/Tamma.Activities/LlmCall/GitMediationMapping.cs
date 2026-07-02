using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Story 38 (Phase 2, Batch A) — shared projection of the git/CI mediation wire
/// records (<see cref="GitCallResponse"/> / <see cref="CiCallResponse"/>) back into
/// the composite integration models (<see cref="GitHubCommit"/> /
/// <see cref="GitHubFileChange"/> / <see cref="TestRunResult"/>) so the eight
/// activities cut over from <c>IIntegrationService</c> to <see cref="TammaApiClient"/>
/// keep their downstream logic byte-compatible. Pure + null-safe (a null / empty wire
/// list projects to an empty list), so it is unit-testable in isolation.
/// </summary>
public static class GitMediationMapping
{
    /// <summary>Project the wire commit summaries into composite <see cref="GitHubCommit"/>
    /// records (empty list when the wire carried none).</summary>
    public static List<GitHubCommit> ToCommits(IReadOnlyList<GitCommitSummaryDto>? commits)
        => commits is null
            ? new List<GitHubCommit>()
            : commits.Select(c => new GitHubCommit
            {
                Sha = c.Sha,
                Message = c.Message,
                Author = c.Author,
                Timestamp = c.Timestamp,
                Additions = c.Additions,
                Deletions = c.Deletions,
                Files = c.Files.ToList(),
            }).ToList();

    /// <summary>Project the wire file changes into composite <see cref="GitHubFileChange"/>
    /// records (empty list when the wire carried none).</summary>
    public static List<GitHubFileChange> ToFileChanges(IReadOnlyList<GitFileChangeDto>? changes)
        => changes is null
            ? new List<GitHubFileChange>()
            : changes.Select(c => new GitHubFileChange
            {
                FilePath = c.FilePath,
                ChangeType = c.ChangeType,
                Additions = c.Additions,
                Deletions = c.Deletions,
            }).ToList();

    /// <summary>Project the wire test-run summary into a composite
    /// <see cref="TestRunResult"/>. <see cref="TestRunResult.FailedTestDetails"/> is
    /// left empty — the CI-mediation endpoint returns aggregate counts only, not
    /// per-test detail. A null summary projects to an empty (all-zero) result.</summary>
    public static TestRunResult ToTestRun(CiTestRunDto? run)
        => run is null
            ? new TestRunResult()
            : new TestRunResult
            {
                RunId = run.RunId,
                Status = run.Status,
                TotalTests = run.TotalTests,
                PassedTests = run.PassedTests,
                FailedTests = run.FailedTests,
                SkippedTests = run.SkippedTests,
                CoveragePercentage = run.CoveragePercentage,
            };

    /// <summary>Story 38 (Phase 2, Batch B) — project the wire build-status summary
    /// into a composite <see cref="BuildStatus"/>. The CI-mediation build-status DTO
    /// carries no <c>Error</c> field (a failed build surfaces via <see cref="BuildStatus.Status"/>);
    /// <see cref="BuildStatus.Error"/> is therefore left null — a genuine mediation
    /// failure is handled upstream by the caller's <c>!Success</c> throw, not here. A
    /// null summary projects to an empty (default) status.</summary>
    public static BuildStatus ToBuildStatus(CiBuildStatusDto? status)
        => status is null
            ? new BuildStatus()
            : new BuildStatus
            {
                Status = status.Status,
                BuildUrl = status.BuildUrl,
                StartedAt = status.StartedAt,
                FinishedAt = status.FinishedAt,
            };

    /// <summary>Story 38 (Phase 2, Batch C) — project a git-merge wire response into the
    /// composite <see cref="GitHubMergeResult"/> so the two mentorship/review merge callers
    /// keep their downstream logic byte-compatible. Soft-fail (write op): a null response
    /// (guard 403 / token 503 / auth 401 / transport) or a <c>Success=false</c> envelope
    /// maps to <c>Success=false</c> with the failure reason/code surfaced on
    /// <see cref="GitHubMergeResult.Error"/> — mirrors the composite's soft merge result
    /// (never a fabricated success).</summary>
    public static GitHubMergeResult ToMergeResult(GitCallResponse? response)
        => response is null
            ? new GitHubMergeResult { Success = false, Error = "git mediation endpoint unavailable" }
            : new GitHubMergeResult
            {
                Success = response.Success,
                MergeSha = response.MergeSha,
                Error = response.Success ? null : (response.FailureReason ?? response.FailureCode),
            };

    /// <summary>Story 38 (Phase 2, Batch C) — project a JIRA-mediation ticket DTO into the
    /// composite <see cref="JiraTicket"/> (null DTO → null, matching the composite's nullable
    /// read). <see cref="JiraTicket.Labels"/> defaults to an empty list when the wire carried
    /// none.</summary>
    public static JiraTicket? ToJiraTicket(JiraTicketDto? dto)
        => dto is null
            ? null
            : new JiraTicket
            {
                Id = dto.Id,
                Key = dto.Key,
                Summary = dto.Summary,
                Description = dto.Description,
                Status = dto.Status,
                Assignee = dto.Assignee,
                Priority = dto.Priority,
                Labels = dto.Labels.ToList(),
            };

    /// <summary>Story 38 (Phase 2, Batch C) — project a JIRA-mediation response into the
    /// composite <see cref="JiraTicketResult"/>. Soft-fail (write op): a null response
    /// (transport / mediation unavailable) or a <c>Success=false</c> envelope maps to
    /// <c>Success=false</c> with the failure reason/code on <see cref="JiraTicketResult.Error"/> —
    /// mirrors the composite's soft update result (the update path never threw for an expected
    /// failure).</summary>
    public static JiraTicketResult ToJiraTicketResult(JiraCallResponse? response)
        => response is null
            ? new JiraTicketResult { Success = false, Error = "jira mediation endpoint unavailable" }
            : new JiraTicketResult
            {
                Success = response.Success,
                TicketKey = response.TicketKey,
                Error = response.Success ? null : (response.FailureReason ?? response.FailureCode),
            };
}
