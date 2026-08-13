using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitLab.Dtos;
using Tamma.Platforms.GitLab.Mapping;

namespace Tamma.Platforms.GitLab.Tests;

[TestFixture]
public sealed class MrToPullRequestMapperTests
{
    [Test]
    public void Maps_opened_state_to_Open()
    {
        MrToPullRequestMapper.MapState("opened").Should().Be(PullRequestState.Open);
    }

    [Test]
    public void Maps_closed_state_to_Closed()
    {
        MrToPullRequestMapper.MapState("closed").Should().Be(PullRequestState.Closed);
    }

    [Test]
    public void Maps_merged_state_to_Merged()
    {
        MrToPullRequestMapper.MapState("merged").Should().Be(PullRequestState.Merged);
    }

    [Test]
    public void Maps_locked_state_to_Closed()
    {
        MrToPullRequestMapper.MapState("locked").Should().Be(PullRequestState.Closed);
    }

    [Test]
    public void Map_combines_draft_and_wip_flags()
    {
        var mr = new GitLabMergeRequest
        {
            Iid = 7,
            Title = "Add feature",
            SourceBranch = "feat/x",
            TargetBranch = "main",
            State = "opened",
            WorkInProgress = true,
            Draft = false,
            Author = new GitLabUser { Username = "alice" },
            CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse("2026-01-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        };
        var pr = MrToPullRequestMapper.Map(mr);
        pr.IsDraft.Should().BeTrue();
        pr.Number.Should().Be("7");
        pr.AuthorLogin.Should().Be("alice");
    }

    [Test]
    public void Map_uses_iid_not_id_for_Number()
    {
        var mr = new GitLabMergeRequest { Id = 99999, Iid = 4, Title = "x" };
        MrToPullRequestMapper.Map(mr).Number.Should().Be("4");
    }

    [Test]
    public void Map_falls_back_to_author_name_if_username_missing()
    {
        var mr = new GitLabMergeRequest
        {
            Iid = 1, Title = "x",
            Author = new GitLabUser { Name = "Bob" },
        };
        MrToPullRequestMapper.Map(mr).AuthorLogin.Should().Be("Bob");
    }

    [Test]
    public void MapFileStatus_NewFile_returns_Added()
    {
        var change = new GitLabMrChange { NewFile = true };
        MrToPullRequestMapper.MapFileStatus(change).Should().Be(PrFileStatus.Added);
    }

    [Test]
    public void MapFileStatus_DeletedFile_returns_Removed()
    {
        var change = new GitLabMrChange { DeletedFile = true };
        MrToPullRequestMapper.MapFileStatus(change).Should().Be(PrFileStatus.Removed);
    }

    [Test]
    public void MapFileStatus_RenamedFile_returns_Renamed()
    {
        var change = new GitLabMrChange { RenamedFile = true };
        MrToPullRequestMapper.MapFileStatus(change).Should().Be(PrFileStatus.Renamed);
    }

    [Test]
    public void MapFileStatus_no_flags_returns_Modified()
    {
        var change = new GitLabMrChange();
        MrToPullRequestMapper.MapFileStatus(change).Should().Be(PrFileStatus.Modified);
    }

    [Test]
    public void CountDiffLines_counts_plus_minus()
    {
        var diff = "@@ -1,3 +1,4 @@\n line1\n+added\n-deleted\n line2\n+added2\n";
        var (add, del) = MrToPullRequestMapper.CountDiffLines(diff);
        add.Should().Be(2);
        del.Should().Be(1);
    }

    [Test]
    public void CountDiffLines_skips_diff_headers()
    {
        var diff = "--- a/file.txt\n+++ b/file.txt\n+real\n-real\n";
        var (add, del) = MrToPullRequestMapper.CountDiffLines(diff);
        add.Should().Be(1);
        del.Should().Be(1);
    }

    [Test]
    public void CountDiffLines_empty_returns_zeros()
    {
        var (add, del) = MrToPullRequestMapper.CountDiffLines(null);
        add.Should().Be(0);
        del.Should().Be(0);
    }

    // ── Epic 31 P6 M1 — draft inferred from the title prefix ONLY when the
    //    booleans are absent (older instances / thin webhook payloads).
    //    Epic 31 review (F-medium) amended the semantics: these cases now
    //    leave Draft/WorkInProgress NULL (payload omitted them) — booleans,
    //    when present, are authoritative (see the test below). ──

    [TestCase("Draft: fix the thing", true)]
    [TestCase("[Draft] fix the thing", true)]
    [TestCase("(Draft) fix the thing", true)]
    [TestCase("WIP: fix the thing", true)]
    [TestCase("[WIP] fix the thing", true)]
    [TestCase("draft: lower-case works too", true)]
    [TestCase("fix the thing", false)]
    [TestCase("Undrafted: not a draft marker", false)]
    [TestCase("Drafting a fix", false)]
    public void Map_infers_IsDraft_from_title_prefix_WhenBooleansAbsent(string title, bool expected)
    {
        var mr = new GitLabMergeRequest
        {
            Iid = 7,
            Title = title,
            State = "opened",
            // Draft / WorkInProgress deliberately left null: the payload
            // omitted the booleans — the only case where inference may run.
        };
        MrToPullRequestMapper.Map(mr).IsDraft.Should().Be(expected);
    }

    // ── Epic 31 review (F-medium) — when the payload CARRIES the booleans
    //    they are authoritative; a stale prefix never vetoes them. GitLab
    //    ≥14.8 treats "WIP:" as ordinary title text, so a WIP-titled MR with
    //    an explicit draft:false is a READY MR. ──

    [Test]
    public void Map_booleansPresent_AreAuthoritative_PrefixNeverVetoes()
    {
        var wipTitledReady = new GitLabMergeRequest
        {
            Iid = 7,
            Title = "WIP: refactor auth",
            State = "opened",
            Draft = false,
            WorkInProgress = false,
        };
        MrToPullRequestMapper.Map(wipTitledReady).IsDraft.Should().BeFalse(
            "the server said draft:false — on ≥14.8 the WIP prefix is just title text, and "
            + "reporting IsDraft=true here made SetDraft(true) silently no-op on a ready MR");

        var draftWithoutPrefix = new GitLabMergeRequest
        {
            Iid = 7,
            Title = "fix the thing",
            State = "opened",
            Draft = true,
            WorkInProgress = false,
        };
        MrToPullRequestMapper.Map(draftWithoutPrefix).IsDraft.Should().BeTrue();
    }

    // ── Epic 31 P6 M2 — merge read-backs (the merge activity fails loud on
    //    a missing SHA; a squash merge reports squash_commit_sha only). ──

    [Test]
    public void Map_surfaces_merge_commit_sha_and_falls_back_to_squash_sha()
    {
        var mergeCommit = new GitLabMergeRequest
        {
            Iid = 5, State = "merged", MergeCommitSha = "merge000",
        };
        MrToPullRequestMapper.Map(mergeCommit).MergeCommitSha.Should().Be("merge000");

        var squashed = new GitLabMergeRequest
        {
            Iid = 5, State = "merged", SquashCommitSha = "squash111",
        };
        MrToPullRequestMapper.Map(squashed).MergeCommitSha.Should().Be("squash111",
            "a squash merge reports squash_commit_sha; merge_commit_sha can be null there");
    }

    // ── Epic 31 review (F-medium) — the THIRD merge shape: a fast-forward
    //    merge (project merge method "ff", squash off) creates no merge
    //    commit and no squash commit, so BOTH SHA fields are null; the merged
    //    head is only in `sha`. Verified against gitlab-org/gitlab
    //    merge_strategies/from_source_branch.rb (fast_forward! returns only
    //    commit_sha, which lands in neither API field). ──

    [Test]
    public void Map_fastForwardMerge_BothShasNull_FallsBackToSha()
    {
        var fastForwarded = new GitLabMergeRequest
        {
            Iid = 5,
            State = "merged",
            Sha = "ffhead222",
            MergeCommitSha = null,
            SquashCommitSha = null,
        };
        MrToPullRequestMapper.Map(fastForwarded).MergeCommitSha.Should().Be("ffhead222",
            "after an ff merge the MR's sha IS the new target tip — without this fallback a "
            + "successful merge was reported as MergeOutcome.Failed(\"api_error\")");
    }

    [Test]
    public void Map_openMr_NeverReportsHeadShaAsMergeSha()
    {
        var open = new GitLabMergeRequest
        {
            Iid = 5,
            State = "opened",
            Sha = "headsha333",
        };
        MrToPullRequestMapper.Map(open).MergeCommitSha.Should().BeNull(
            "the sha fallback is gated on state=merged — an open MR's head SHA must never "
            + "masquerade as a merge SHA");
    }

    [TestCase("mergeable", null, true)]
    [TestCase(null, "can_be_merged", true)]
    [TestCase("conflict", "cannot_be_merged", false)]
    [TestCase(null, "cannot_be_merged", false)]
    [TestCase("checking", null, null)]
    [TestCase("unchecked", "unchecked", null)]
    [TestCase("draft_status", "can_be_merged", true)]
    [TestCase(null, null, null)]
    public void MapMergeable_maps_only_confirmed_shapes(
        string? detailed, string? legacy, bool? expected)
    {
        var mr = new GitLabMergeRequest
        {
            Iid = 5, DetailedMergeStatus = detailed, MergeStatus = legacy,
        };
        MrToPullRequestMapper.MapMergeable(mr).Should().Be(expected);
    }

    [Test]
    public void DraftTitle_helpers_add_and_strip_prefixes()
    {
        GitLabDraftTitle.AddDraftPrefix("fix").Should().Be("Draft: fix");
        GitLabDraftTitle.AddDraftPrefix("Draft: fix").Should().Be("Draft: fix",
            "adding to an already-drafted title is idempotent");
        GitLabDraftTitle.StripDraftPrefix("Draft: fix").Should().Be("fix");
        GitLabDraftTitle.StripDraftPrefix("Draft: [WIP] fix").Should().Be("fix",
            "stacked prefixes strip in one call");
        GitLabDraftTitle.StripDraftPrefix("fix").Should().Be("fix");
    }
}
