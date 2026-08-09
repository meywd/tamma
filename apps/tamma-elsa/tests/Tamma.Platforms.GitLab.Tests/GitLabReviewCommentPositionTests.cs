using System.Net;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitLab.Tests.Support;

namespace Tamma.Platforms.GitLab.Tests;

/// <summary>
/// Epic 31 P6 M1 — review-comment position hardening. The old driver sent
/// <c>base_sha = start_sha = head_sha = CommitSha</c>, which GitLab's
/// line-code validation 400s on any MULTI-COMMIT MR (base/start/head are
/// three different SHAs there). The driver now fetches the MR's
/// <c>diff_refs</c> (single-MR GET) and uses its real SHAs, falling back
/// to the caller's CommitSha only when <c>diff_refs</c> is absent — the
/// API doc marks it "empty when the merge request is created, populates
/// asynchronously".
/// </summary>
[TestFixture]
public class GitLabReviewCommentPositionTests
{
    private const string DiscussionJson =
        """{"id":"d-1","notes":[{"id":42,"body":"please fix","author":{"username":"reviewer"},"created_at":"2026-01-01T00:00:00Z"}]}""";

    /// <summary>The 2-commit-MR shape: base, start and head are three
    /// distinct SHAs (head is the MR's 2nd commit; the caller passes the
    /// 1st commit's SHA, which must NOT end up in the position).</summary>
    private const string TwoCommitMrJson =
        """
        {"id":100,"iid":3,"title":"feat","state":"opened","draft":false,
          "source_branch":"feat/x","target_branch":"main",
          "author":{"username":"bot","id":7},
          "created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z",
          "sha":"commit2-head",
          "diff_refs":{"base_sha":"base000","start_sha":"start111","head_sha":"commit2-head"}}
        """;

    [Test]
    public async Task LineComment_OnTwoCommitMr_UsesDiffRefs_NotTheCallerSha()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.AddRoute(HttpMethod.Get, "/merge_requests/3",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, TwoCommitMrJson));
        handler.AddRoute(HttpMethod.Post, "/merge_requests/3/discussions",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.Created, DiscussionJson));

        var result = await client.CreatePullRequestReviewCommentAsync(
            new CreatePullRequestReviewCommentRequest(
                "g", "p", "3", "src/foo.cs", 10, "please fix",
                CommitSha: "commit1-not-head"));

        result.Should().BeOfType<PlatformResult<IssueComment>.Ok>();

        var post = handler.Requests.Single(r =>
            r.Method == HttpMethod.Post
            && r.RequestUri.ToString().Contains("/discussions", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(post.Body!);
        var position = doc.RootElement.GetProperty("position");
        position.GetProperty("base_sha").GetString().Should().Be("base000");
        position.GetProperty("start_sha").GetString().Should().Be("start111");
        position.GetProperty("head_sha").GetString().Should().Be("commit2-head",
            "the MR's diff_refs — not the caller's single-commit SHA — anchor the position");
        position.GetProperty("new_path").GetString().Should().Be("src/foo.cs");
        position.GetProperty("new_line").GetInt32().Should().Be(10);
        post.Body.Should().NotContain("commit1-not-head");
    }

    [Test]
    public async Task LineComment_WhenDiffRefsAbsent_FallsBackToCallerSha()
    {
        // Fresh-MR race: diff_refs populates asynchronously; the driver keeps
        // the old single-SHA best-effort shape instead of failing the comment.
        var (client, handler) = TestFactory.BuildClient();
        handler.AddRoute(HttpMethod.Get, "/merge_requests/3",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """
                {"id":100,"iid":3,"title":"feat","state":"opened",
                  "source_branch":"feat/x","target_branch":"main",
                  "author":{"username":"bot","id":7},
                  "created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}
                """));
        handler.AddRoute(HttpMethod.Post, "/merge_requests/3/discussions",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.Created, DiscussionJson));

        var result = await client.CreatePullRequestReviewCommentAsync(
            new CreatePullRequestReviewCommentRequest(
                "g", "p", "3", "src/foo.cs", 10, "please fix", "abc123"));

        result.Should().BeOfType<PlatformResult<IssueComment>.Ok>();
        var post = handler.Requests.Single(r =>
            r.Method == HttpMethod.Post
            && r.RequestUri.ToString().Contains("/discussions", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(post.Body!);
        var position = doc.RootElement.GetProperty("position");
        position.GetProperty("base_sha").GetString().Should().Be("abc123");
        position.GetProperty("head_sha").GetString().Should().Be("abc123");
    }

    [Test]
    public async Task LineComment_WhenMrLookupFails_StillPostsWithFallbackShas()
    {
        // The positioning lookup is best-effort — a 500 on the GET must not
        // fail the comment; the POST carries the honest platform answer.
        var (client, handler) = TestFactory.BuildClient();
        handler.AddRoute(HttpMethod.Get, "/merge_requests/3",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "{}"));
        handler.AddRoute(HttpMethod.Post, "/merge_requests/3/discussions",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.Created, DiscussionJson));

        var result = await client.CreatePullRequestReviewCommentAsync(
            new CreatePullRequestReviewCommentRequest(
                "g", "p", "3", "src/foo.cs", 10, "please fix", "abc123"));

        result.Should().BeOfType<PlatformResult<IssueComment>.Ok>();
    }
}
