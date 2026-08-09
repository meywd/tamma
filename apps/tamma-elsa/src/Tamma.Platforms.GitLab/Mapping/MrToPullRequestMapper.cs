using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitLab.Dtos;

namespace Tamma.Platforms.GitLab.Mapping;

/// <summary>
/// Story 31-6 §Step 6 — translate GitLab Merge Request DTO to the
/// neutral <see cref="PullRequest"/> record.
///
/// <para>GitLab API quirks the mapper handles:</para>
/// <list type="bullet">
///   <item><b>State enum</b>: <c>opened</c> / <c>closed</c> /
///         <c>merged</c> / <c>locked</c> mapped to
///         <see cref="PullRequestState"/>. <c>locked</c> collapses to
///         <see cref="PullRequestState.Closed"/> since callers don't
///         have a separate concept.</item>
///   <item><b>Draft flag</b>: GitLab uses both <c>draft</c> (newer) and
///         <c>work_in_progress</c> (legacy). Either being true sets
///         <see cref="PullRequest.IsDraft"/>; Epic 31 P6 M1 also infers
///         draft from the <c>Draft:</c>/<c>[Draft]</c>/<c>(Draft)</c> (and
///         legacy <c>WIP</c>) title prefixes via
///         <see cref="GitLabDraftTitle"/> for payloads that omit the
///         booleans.</item>
///   <item><b>Number</b>: GitLab MRs have two ids — <c>id</c>
///         (global) and <c>iid</c> (per-project). Callers always
///         address by <c>iid</c>, so the mapper surfaces that.</item>
/// </list>
/// </summary>
internal static class MrToPullRequestMapper
{
    public static PullRequest Map(GitLabMergeRequest mr)
    {
        ArgumentNullException.ThrowIfNull(mr);

        return new PullRequest(
            Number: mr.Iid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Title: mr.Title ?? string.Empty,
            Body: mr.Description,
            SourceBranch: mr.SourceBranch ?? string.Empty,
            TargetBranch: mr.TargetBranch ?? string.Empty,
            State: MapState(mr.State),
            IsDraft: mr.Draft || mr.WorkInProgress || GitLabDraftTitle.HasDraftPrefix(mr.Title),
            HtmlUrl: mr.WebUrl ?? string.Empty,
            AuthorLogin: mr.Author?.Username ?? mr.Author?.Name ?? string.Empty,
            CreatedAt: mr.CreatedAt,
            UpdatedAt: mr.UpdatedAt)
        {
            // Epic 31 P6 M2 — merge read-backs. The merge activity fails loud
            // on a missing SHA, so both merge shapes are covered: a merge
            // commit reports merge_commit_sha; a squash merge reports
            // squash_commit_sha (merge_commit_sha can be null there).
            MergeCommitSha = mr.MergeCommitSha ?? mr.SquashCommitSha,
            Mergeable = MapMergeable(mr),
            MergeableState = mr.DetailedMergeStatus ?? mr.MergeStatus,
        };
    }

    /// <summary>
    /// Platform-computed mergeability. Only POSITIVE confirmations map:
    /// <c>can_be_merged</c>/<c>mergeable</c> → true; the CONFIRMED conflict
    /// shapes (<c>cannot_be_merged</c> legacy, <c>conflict</c> detailed) →
    /// false; anything else (unchecked / checking / absent / the
    /// non-conflict blocked reasons like <c>draft_status</c>) → null so the
    /// merge activity's conflict gate never fires on a still-computing or
    /// merely-blocked MR.
    /// </summary>
    public static bool? MapMergeable(GitLabMergeRequest mr)
    {
        ArgumentNullException.ThrowIfNull(mr);
        if (string.Equals(mr.DetailedMergeStatus, "mergeable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mr.MergeStatus, "can_be_merged", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.Equals(mr.DetailedMergeStatus, "conflict", StringComparison.OrdinalIgnoreCase)
            || (mr.DetailedMergeStatus is null
                && string.Equals(mr.MergeStatus, "cannot_be_merged", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        return null;
    }

    public static PullRequestState MapState(string? state) => state switch
    {
        "opened" => PullRequestState.Open,
        "closed" or "locked" => PullRequestState.Closed,
        "merged" => PullRequestState.Merged,
        _ => PullRequestState.Closed,
    };

    public static PrFileStatus MapFileStatus(GitLabMrChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.NewFile) return PrFileStatus.Added;
        if (change.DeletedFile) return PrFileStatus.Removed;
        if (change.RenamedFile) return PrFileStatus.Renamed;
        return PrFileStatus.Modified;
    }

    /// <summary>
    /// Diff additions/deletions counter — GitLab doesn't include the
    /// counts in <c>changes</c>, so the mapper grep-counts the unified
    /// diff text. Slow for huge diffs; callers that need exact counts
    /// can branch on <see cref="PrFile.Path"/>.
    /// </summary>
    public static (int Additions, int Deletions) CountDiffLines(string? diff)
    {
        if (string.IsNullOrEmpty(diff)) return (0, 0);
        var add = 0;
        var del = 0;
        foreach (var line in diff.Split('\n'))
        {
            if (line.Length == 0) continue;
            // Ignore the +++/--- header lines.
            if (line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }
            if (line[0] == '+') add++;
            else if (line[0] == '-') del++;
        }
        return (add, del);
    }
}
