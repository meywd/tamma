using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitHub;

namespace Tamma.Platforms.GitHub.Tests;

/// <summary>
/// Story 31-3 — model translation tests covering the
/// <see cref="GitHubModelMapper"/>. Each translator goes through one
/// happy path + one boundary case (null conclusion, missing fields).
/// </summary>
[TestFixture]
public sealed class GitHubModelMapperTests
{
    [Test]
    public void ToWorkflowRun_projects_summary_into_neutral_record()
    {
        var summary = new WorkflowRunSummary(
            Id: 7843219L,
            Status: "completed",
            Conclusion: "success",
            HtmlUrl: "https://github.com/acme/repo/actions/runs/7843219",
            CreatedAt: new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc),
            UpdatedAt: new DateTime(2026, 4, 21, 12, 5, 0, DateTimeKind.Utc),
            HeadBranch: "feat/foo",
            Event: "workflow_dispatch",
            ArtifactsUrl: "https://api.github.com/repos/acme/repo/actions/runs/7843219/artifacts");

        var run = GitHubModelMapper.ToWorkflowRun(summary);

        run.RunId.Should().Be("7843219");
        run.Status.Should().Be("completed");
        run.Conclusion.Should().Be("success");
        run.HtmlUrl.Should().Be(summary.HtmlUrl);
        run.StartedAt.Should().Be(new DateTimeOffset(summary.CreatedAt, TimeSpan.Zero));
        run.CompletedAt.Should().Be(new DateTimeOffset(summary.UpdatedAt, TimeSpan.Zero));
        run.RawMetadata.Should().BeNull();
    }

    [Test]
    public void ToWorkflowRun_treats_empty_conclusion_as_running()
    {
        var summary = new WorkflowRunSummary(
            Id: 1L,
            Status: "in_progress",
            Conclusion: "", // still running
            HtmlUrl: "https://github.com/acme/repo/actions/runs/1",
            CreatedAt: new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc),
            UpdatedAt: new DateTime(2026, 4, 21, 12, 1, 0, DateTimeKind.Utc),
            HeadBranch: "main",
            Event: "workflow_dispatch",
            ArtifactsUrl: "");

        var run = GitHubModelMapper.ToWorkflowRun(summary);

        run.Status.Should().Be("in_progress");
        run.Conclusion.Should().BeNull();
        run.CompletedAt.Should().BeNull();
    }

    [Test]
    public void ToWorkflowRun_rejects_null_summary()
    {
        Action act = () => GitHubModelMapper.ToWorkflowRun(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void ToPullRequest_projects_summary_into_neutral_record()
    {
        var summary = new PullRequestSummary(
            Number: 42,
            Title: "feat: add gizmo",
            Body: "Closes #41",
            HtmlUrl: "https://github.com/acme/repo/pull/42",
            HeadSha: "abc123",
            ChangedFiles: 3);

        var pr = GitHubModelMapper.ToPullRequest(summary, headBranch: "feat/gizmo");

        pr.Number.Should().Be("42");
        pr.Title.Should().Be("feat: add gizmo");
        pr.Body.Should().Be("Closes #41");
        pr.SourceBranch.Should().Be("feat/gizmo");
        pr.HtmlUrl.Should().Be(summary.HtmlUrl);
        pr.State.Should().Be(PullRequestState.Open);
        pr.IsDraft.Should().BeFalse();
    }

    [Test]
    public void ToPullRequest_rejects_null_summary()
    {
        Action act = () => GitHubModelMapper.ToPullRequest(null!, headBranch: "main");
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void ToPullRequest_rejects_blank_head_branch()
    {
        var summary = new PullRequestSummary(1, "x", null, "u", "sha", 0);
        Action act = () => GitHubModelMapper.ToPullRequest(summary, headBranch: "  ");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ToArtifact_projects_artifact_into_neutral_record()
    {
        var artifact = new WorkflowRunArtifact(
            Id: 9876543L,
            Name: "agent-result.zip",
            SizeInBytes: 524_288L,
            Expired: false);

        var neutral = GitHubModelMapper.ToArtifact(artifact);

        neutral.Id.Should().Be("9876543");
        neutral.Name.Should().Be("agent-result.zip");
        neutral.SizeBytes.Should().Be(524_288L);
        neutral.DownloadUrl.Should().BeEmpty();
    }

    [Test]
    public void ToArtifact_rejects_null_artifact()
    {
        Action act = () => GitHubModelMapper.ToArtifact(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void ToBranchFromCommit_builds_neutral_branch_record()
    {
        var branch = GitHubModelMapper.ToBranchFromCommit("main", "deadbeef");
        branch.Name.Should().Be("main");
        branch.Sha.Should().Be("deadbeef");
        branch.Protected.Should().BeFalse();
    }
}
