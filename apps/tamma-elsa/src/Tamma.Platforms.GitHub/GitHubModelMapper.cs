using Tamma.Activities.AgentDispatch;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Story 31-3 — projection layer between Tamma's existing GitHub
/// summary records (<see cref="WorkflowRunSummary"/>,
/// <see cref="PullRequestSummary"/>, <see cref="WorkflowRunArtifact"/>)
/// and the platform-neutral
/// <see cref="Tamma.Platforms.Abstractions.Models"/> records used by
/// every <see cref="Tamma.Platforms.Abstractions.IGitPlatformDriver"/>.
///
/// <para>The mapper is internal-only — callers go through the driver,
/// not the mapper. <c>InternalsVisibleTo</c> on
/// <c>Tamma.Platforms.GitHub.Tests</c> exposes the helpers for unit
/// coverage.</para>
///
/// <para>Status &amp; conclusion strings stay verbatim per the
/// <see cref="WorkflowRun"/> contract — the abstraction does not
/// normalize platform-native vocabulary.</para>
/// </summary>
internal static class GitHubModelMapper
{
    /// <summary>
    /// Project a <see cref="WorkflowRunSummary"/> into the neutral
    /// <see cref="WorkflowRun"/>. <see cref="WorkflowRunSummary"/>
    /// already carries verbatim GitHub status / conclusion strings —
    /// we surface them as-is (empty conclusion → null since the
    /// neutral record uses null for "still running").
    /// </summary>
    public static WorkflowRun ToWorkflowRun(WorkflowRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var conclusion = string.IsNullOrEmpty(summary.Conclusion) ? null : summary.Conclusion;
        var startedAt = new DateTimeOffset(
            DateTime.SpecifyKind(summary.CreatedAt, DateTimeKind.Utc),
            TimeSpan.Zero);
        DateTimeOffset? completedAt = null;
        if (!string.IsNullOrEmpty(conclusion))
        {
            completedAt = new DateTimeOffset(
                DateTime.SpecifyKind(summary.UpdatedAt, DateTimeKind.Utc),
                TimeSpan.Zero);
        }
        return new WorkflowRun(
            RunId: summary.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Status: summary.Status,
            Conclusion: conclusion,
            HtmlUrl: summary.HtmlUrl,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            RawMetadata: null);
    }

    /// <summary>
    /// Project a <see cref="PullRequestSummary"/> (the slice
    /// <see cref="IGitHubActionsClient.ListPullRequestsForHeadAsync"/>
    /// returns) into the neutral <see cref="PullRequest"/>.
    ///
    /// <para>The summary doesn't carry state / draft / branch metadata
    /// (it was shaped for agent-dispatch needs). We default state to
    /// <see cref="PullRequestState.Open"/> because GitHub only
    /// surfaces head-branch matches when the PR is live; older closed
    /// PRs don't show up. <see cref="PullRequest.IsDraft"/> defaults
    /// to false since the summary doesn't carry it.</para>
    /// </summary>
    public static PullRequest ToPullRequest(PullRequestSummary summary, string headBranch)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(headBranch);
        return new PullRequest(
            Number: summary.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Title: summary.Title,
            Body: summary.Body,
            SourceBranch: headBranch,
            TargetBranch: string.Empty, // unknown from the narrow summary; future work.
            State: PullRequestState.Open,
            IsDraft: false,
            HtmlUrl: summary.HtmlUrl,
            AuthorLogin: "unknown",
            CreatedAt: DateTimeOffset.MinValue,
            UpdatedAt: DateTimeOffset.MinValue);
    }

    /// <summary>
    /// Project a GitHub Actions <see cref="WorkflowRunArtifact"/> into
    /// the neutral <see cref="Artifact"/>.
    /// </summary>
    public static Artifact ToArtifact(WorkflowRunArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new Artifact(
            Id: artifact.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Name: artifact.Name,
            SizeBytes: artifact.SizeInBytes,
            DownloadUrl: string.Empty); // GitHub mints redirect URLs at download time.
    }

    /// <summary>
    /// Project a GitHub <see cref="CompareCommit"/> into the neutral
    /// <see cref="Branch"/> shape — used by
    /// <see cref="GitHubPlatformClient.ListRepoBranchesAsync"/>'s
    /// best-effort backing path. The Compare API does not return
    /// branch protection status, so <see cref="Branch.Protected"/> is
    /// always false from this path.
    /// </summary>
    public static Branch ToBranchFromCommit(string branchName, string sha) =>
        new(Name: branchName, Sha: sha, Protected: false);
}
