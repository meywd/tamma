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
}
