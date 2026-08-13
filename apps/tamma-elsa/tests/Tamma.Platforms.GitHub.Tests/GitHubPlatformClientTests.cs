using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.GitHub.Tests;

/// <summary>
/// Epic 31 P1 stage 2 — unit tests for the REAL
/// <see cref="GitHubPlatformClient"/> over a scripted
/// <see cref="FakeHttpMessageHandler"/> (the Gitea test house style):
/// per-verb happy paths, the six 31-13 lifecycle verbs, the GraphQL
/// set-draft (github.com and GHES URL shapes), the loop verbs, the
/// error-classification parity pins, and the probe-fails-on-junk-token
/// contract.
/// </summary>
[TestFixture]
public sealed class GitHubPlatformClientTests
{
    private const string Api = "https://api.github.com";

    private static (GitHubPlatformClient Client, FakeHttpMessageHandler Handler) Build(
        string baseUrl = Api, bool appMode = false)
    {
        var handler = new FakeHttpMessageHandler();
        var http = new GitHubHttpClient(
            new HttpClient(handler), baseUrl, new GitHubAuth.Pat("test-token"));
        return (new GitHubPlatformClient(http, "github.com", appMode), handler);
    }

    private static string PrJson(
        long number = 7,
        string state = "open",
        bool merged = false,
        bool draft = false,
        string nodeId = "PR_node1") =>
        $$"""
        {
          "number": {{number}}, "node_id": "{{nodeId}}", "title": "T", "body": "b",
          "state": "{{state}}", "merged": {{(merged ? "true" : "false")}},
          "draft": {{(draft ? "true" : "false")}},
          "html_url": "https://github.com/o/r/pull/{{number}}",
          "user": { "login": "alice" },
          "head": { "ref": "feat", "sha": "headsha" },
          "base": { "ref": "main" },
          "labels": [ { "name": "bug" } ],
          "created_at": "2026-08-01T00:00:00Z", "updated_at": "2026-08-02T00:00:00Z"
        }
        """;

    // ================================================================
    // Repo / branch / file reads
    // ================================================================

    [Test]
    public async Task GetRepo_maps_fields()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r", HttpStatusCode.OK,
            """
            { "name": "r", "owner": { "login": "o" }, "default_branch": "trunk",
              "private": true, "description": "d",
              "clone_url": "https://github.com/o/r.git",
              "html_url": "https://github.com/o/r" }
            """);

        var result = await client.GetRepoAsync("o", "r");

        var repo = result.Should().BeOfType<PlatformResult<Repo>.Ok>().Subject.Value;
        repo.Host.Should().Be("github.com");
        repo.Owner.Should().Be("o");
        repo.Name.Should().Be("r");
        repo.DefaultBranch.Should().Be("trunk");
        repo.IsPrivate.Should().BeTrue();
    }

    [Test]
    public async Task GetRepo_sends_bearer_token()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r", HttpStatusCode.OK, """{ "name": "r" }""");

        await client.GetRepoAsync("o", "r");

        handler.Requests.Single().Headers["Authorization"].Should().Be("Bearer test-token");
    }

    [Test]
    public async Task ListRepoBranches_pages_until_partial_page()
    {
        var (client, handler) = Build();
        var fullPage = "[" + string.Join(",", Enumerable.Range(0, 100).Select(i =>
            $$"""{ "name": "b{{i}}", "commit": { "sha": "s{{i}}" }, "protected": false }""")) + "]";
        handler.EnqueueJson(HttpMethod.Get,
            $"{Api}/repos/o/r/branches?per_page=100&page=1", HttpStatusCode.OK, fullPage);
        handler.EnqueueJson(HttpMethod.Get,
            $"{Api}/repos/o/r/branches?per_page=100&page=2", HttpStatusCode.OK,
            """[ { "name": "last", "commit": { "sha": "sl" }, "protected": true } ]""");

        var result = await client.ListRepoBranchesAsync("o", "r");

        var branches = result.Should().BeOfType<PlatformResult<IReadOnlyList<Branch>>.Ok>().Subject.Value;
        branches.Should().HaveCount(101);
        branches[^1].Should().Be(new Branch("last", "sl", true));
    }

    [Test]
    public async Task GetFileContent_decodes_base64_with_newlines()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/contents/src/a.txt?ref=main",
            HttpStatusCode.OK,
            """{ "type": "file", "encoding": "base64", "content": "aGVsbG8g\nd29ybGQ=" }""");

        var result = await client.GetFileContentAsync(
            new GetFileContentRequest("o", "r", "src/a.txt", "main"));

        System.Text.Encoding.UTF8.GetString(result.GetValueOrDefault()!).Should().Be("hello world");
    }

    [Test]
    public async Task CreateBranch_posts_ref_and_sha()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/git/refs", HttpStatusCode.Created,
            """{ "ref": "refs/heads/feat", "object": { "sha": "basesha" } }""");

        var result = await client.CreateBranchAsync(
            new CreateBranchRequest("o", "r", "feat", "basesha"));

        var branch = result.Should().BeOfType<PlatformResult<Branch>.Ok>().Subject.Value;
        branch.Name.Should().Be("feat");
        branch.Sha.Should().Be("basesha");
        handler.Requests.Single().Body.Should().Contain("refs/heads/feat").And.Contain("basesha");
    }

    // ================================================================
    // Pull requests
    // ================================================================

    [Test]
    public async Task OpenPullRequest_creates_with_draft_flag()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/pulls?state=open",
            HttpStatusCode.OK, "[]");
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/pulls",
            HttpStatusCode.Created, PrJson(draft: true));

        var result = await client.OpenPullRequestAsync(
            new OpenPullRequestRequest("o", "r", "T", "feat", "main", "b", IsDraft: true));

        var pr = result.Should().BeOfType<PlatformResult<PullRequest>.Ok>().Subject.Value;
        pr.Number.Should().Be("7");
        pr.IsDraft.Should().BeTrue();
        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        post.Body.Should().Contain("\"draft\":true").And.Contain("\"head\":\"feat\"");
    }

    [Test]
    public async Task OpenPullRequest_returns_existing_open_pr_idempotently()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/pulls?state=open",
            HttpStatusCode.OK, "[" + PrJson(number: 41) + "]");

        var result = await client.OpenPullRequestAsync(
            new OpenPullRequestRequest("o", "r", "T", "feat", "main"));

        result.GetValueOrDefault()!.Number.Should().Be("41");
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post,
            "an existing (head, base) PR short-circuits creation");
    }

    [Test]
    public async Task GetPullRequest_maps_merged_state()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/pulls/7",
            HttpStatusCode.OK, PrJson(state: "closed", merged: true));

        var result = await client.GetPullRequestAsync("o", "r", "7");

        result.GetValueOrDefault()!.State.Should().Be(PullRequestState.Merged);
    }

    [Test]
    public async Task ListPullRequestFiles_maps_status()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/pulls/7/files",
            HttpStatusCode.OK,
            """
            [ { "filename": "a.cs", "status": "added", "additions": 5, "deletions": 0 },
              { "filename": "b.cs", "status": "unchanged", "additions": 0, "deletions": 0 } ]
            """);

        var result = await client.ListPullRequestFilesAsync("o", "r", "7");

        var files = result.GetValueOrDefault()!;
        files[0].Status.Should().Be(PrFileStatus.Added);
        files[1].Status.Should().Be(PrFileStatus.Other);
    }

    [Test]
    public async Task CreatePullRequestReviewComment_posts_line_anchor()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/pulls/7/comments",
            HttpStatusCode.Created,
            """{ "id": 9, "body": "fix", "user": { "login": "bot" }, "created_at": "2026-08-01T00:00:00Z" }""");

        var result = await client.CreatePullRequestReviewCommentAsync(
            new CreatePullRequestReviewCommentRequest("o", "r", "7", "a.cs", 12, "fix", "headsha"));

        result.GetValueOrDefault()!.Id.Should().Be("9");
        var body = handler.Requests.Single().Body!;
        body.Should().Contain("\"commit_id\":\"headsha\"")
            .And.Contain("\"line\":12")
            .And.Contain("\"side\":\"RIGHT\"");
    }

    [Test]
    public async Task MergePullRequest_puts_method_then_refetches_pr()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Put, $"{Api}/repos/o/r/pulls/7/merge",
            HttpStatusCode.OK, """{ "merged": true, "sha": "mergesha" }""");
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/pulls/7",
            HttpStatusCode.OK, PrJson(state: "closed", merged: true));

        var result = await client.MergePullRequestAsync(
            new MergePullRequestRequest("o", "r", "7", MergeMethod.Squash));

        result.GetValueOrDefault()!.State.Should().Be(PullRequestState.Merged);
        handler.Requests.First().Body.Should().Contain("\"merge_method\":\"squash\"");
    }

    [Test]
    public async Task MergePullRequest_405_maps_to_not_mergeable()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Put, $"{Api}/repos/o/r/pulls/7/merge",
            HttpStatusCode.MethodNotAllowed, """{ "message": "Pull Request is not mergeable" }""");

        var result = await client.MergePullRequestAsync(
            new MergePullRequestRequest("o", "r", "7", MergeMethod.Merge));

        var err = result.Should().BeOfType<PlatformResult<PullRequest>.Failed>().Subject.Error;
        err.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("not_mergeable");
    }

    // ================================================================
    // Story 31-13 — the six lifecycle verbs
    // ================================================================

    [Test]
    public async Task ClosePullRequest_patches_state_closed()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Patch, $"{Api}/repos/o/r/pulls/7",
            HttpStatusCode.OK, PrJson(state: "closed"));

        var result = await client.ClosePullRequestAsync("o", "r", "7");

        result.GetValueOrDefault()!.State.Should().Be(PullRequestState.Closed);
        handler.Requests.Single().Body.Should().Contain("\"state\":\"closed\"");
    }

    [Test]
    public async Task ReopenPullRequest_patches_state_open()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Patch, $"{Api}/repos/o/r/pulls/7",
            HttpStatusCode.OK, PrJson(state: "open"));

        var result = await client.ReopenPullRequestAsync("o", "r", "7");

        result.GetValueOrDefault()!.State.Should().Be(PullRequestState.Open);
        handler.Requests.Single().Body.Should().Contain("\"state\":\"open\"");
    }

    [Test]
    public async Task RequestReviewers_posts_reviewers_and_maps_pr()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/pulls/7/requested_reviewers",
            HttpStatusCode.Created, PrJson());

        var result = await client.RequestReviewersAsync(
            new RequestReviewersRequest("o", "r", "7", ["alice", "bob"], ["team-x"]));

        result.IsOk.Should().BeTrue();
        var body = handler.Requests.Single().Body!;
        body.Should().Contain("alice").And.Contain("bob").And.Contain("team_reviewers");
    }

    [Test]
    public async Task AddPullRequestLabels_posts_then_refetches_pr()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/issues/7/labels",
            HttpStatusCode.OK, """[ { "name": "bug" } ]""");
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/pulls/7",
            HttpStatusCode.OK, PrJson());

        var result = await client.AddPullRequestLabelsAsync(
            new AddPullRequestLabelsRequest("o", "r", "7", ["bug"]));

        result.IsOk.Should().BeTrue();
    }

    [Test]
    public async Task RemovePullRequestLabel_absent_label_is_idempotent_success()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Delete, $"{Api}/repos/o/r/issues/7/labels/bug",
            HttpStatusCode.NotFound, """{ "message": "Label does not exist" }""");
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/pulls/7",
            HttpStatusCode.OK, PrJson());

        var result = await client.RemovePullRequestLabelAsync("o", "r", "7", "bug");

        result.IsOk.Should().BeTrue("404 label removal is idempotent success");
    }

    // ================================================================
    // GraphQL set-draft — cloud and GHES endpoint shapes
    // ================================================================

    [Test]
    public async Task SetDraft_on_cloud_posts_mutation_to_graphql_endpoint()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/pulls/7",
            HttpStatusCode.OK, PrJson(nodeId: "PR_node7"));
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/graphql", HttpStatusCode.OK,
            """{ "data": { "markPullRequestReadyForReview": { "pullRequest": { "isDraft": false, "state": "OPEN", "number": 7 } } } }""");
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/pulls/7",
            HttpStatusCode.OK, PrJson(draft: false));

        var result = await client.SetDraftAsync(
            new SetPullRequestDraftRequest("o", "r", "7", Draft: false));

        result.GetValueOrDefault()!.IsDraft.Should().BeFalse();
        var gql = handler.Requests.Single(r => r.Url == $"{Api}/graphql");
        gql.Body.Should().Contain("markPullRequestReadyForReview").And.Contain("PR_node7");
    }

    [Test]
    public async Task SetDraft_on_ghes_posts_mutation_to_api_graphql_endpoint()
    {
        const string ghes = "https://github.acme.corp/api/v3";
        var (client, handler) = Build(baseUrl: ghes);
        handler.EnqueueJson(HttpMethod.Get, $"{ghes}/repos/o/r/pulls/7",
            HttpStatusCode.OK, PrJson(nodeId: "PR_node7"));
        handler.EnqueueJson(HttpMethod.Post, "https://github.acme.corp/api/graphql",
            HttpStatusCode.OK,
            """{ "data": { "convertPullRequestToDraft": { "pullRequest": { "isDraft": true, "state": "OPEN", "number": 7 } } } }""");
        handler.EnqueueJson(HttpMethod.Get, $"{ghes}/repos/o/r/pulls/7",
            HttpStatusCode.OK, PrJson(draft: true));

        var result = await client.SetDraftAsync(
            new SetPullRequestDraftRequest("o", "r", "7", Draft: true));

        result.GetValueOrDefault()!.IsDraft.Should().BeTrue();
        handler.Requests.Should().Contain(r =>
            r.Url == "https://github.acme.corp/api/graphql" && r.Method == HttpMethod.Post);
    }

    [Test]
    public async Task SetDraft_graphql_errors_map_to_typed_invalid_request()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/pulls/7",
            HttpStatusCode.OK, PrJson(nodeId: "PR_node7"));
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/graphql", HttpStatusCode.OK,
            """{ "data": null, "errors": [ { "message": "boom" } ] }""");

        var result = await client.SetDraftAsync(
            new SetPullRequestDraftRequest("o", "r", "7", Draft: true));

        var err = result.Should().BeOfType<PlatformResult<PullRequest>.Failed>().Subject.Error;
        err.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("graphql_errors");
    }

    // ================================================================
    // Epic 31 P1 — loop verbs
    // ================================================================

    [Test]
    public async Task CloseIssue_posts_comment_then_patches_state()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/issues/3/comments",
            HttpStatusCode.Created,
            """{ "id": 1, "body": "done", "user": { "login": "bot" }, "created_at": "2026-08-01T00:00:00Z" }""");
        handler.EnqueueJson(HttpMethod.Patch, $"{Api}/repos/o/r/issues/3",
            HttpStatusCode.OK,
            """{ "number": 3, "title": "t", "state": "closed", "html_url": "u", "labels": [ { "name": "bug" } ] }""");

        var result = await client.CloseIssueAsync("o", "r", "3", comment: "done");

        var issue = result.Should().BeOfType<PlatformResult<Issue>.Ok>().Subject.Value;
        issue.State.Should().Be(IssueState.Closed);
        issue.Labels.Should().BeEquivalentTo("bug");
        handler.Requests.Should().HaveCount(2);
    }

    [Test]
    public async Task CloseIssue_without_comment_skips_comment_call()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Patch, $"{Api}/repos/o/r/issues/3",
            HttpStatusCode.OK,
            """{ "number": 3, "title": "t", "state": "closed", "html_url": "u", "labels": [] }""");

        var result = await client.CloseIssueAsync("o", "r", "3");

        result.IsOk.Should().BeTrue();
        handler.Requests.Should().HaveCount(1);
    }

    [Test]
    public async Task AddIssueLabels_returns_full_label_set()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/issues/3/labels",
            HttpStatusCode.OK, """[ { "name": "bug" }, { "name": "p1" } ]""");

        var result = await client.AddIssueLabelsAsync(
            new AddIssueLabelsRequest("o", "r", "3", ["p1"]));

        result.GetValueOrDefault().Should().BeEquivalentTo("bug", "p1");
    }

    [Test]
    public async Task RemoveIssueLabel_returns_remaining_labels()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Delete, $"{Api}/repos/o/r/issues/3/labels/bug",
            HttpStatusCode.OK, """[ { "name": "p1" } ]""");

        var result = await client.RemoveIssueLabelAsync("o", "r", "3", "bug");

        result.GetValueOrDefault().Should().BeEquivalentTo("p1");
    }

    [Test]
    public async Task RemoveIssueLabel_absent_label_is_idempotent_and_refetches_labels()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Delete, $"{Api}/repos/o/r/issues/3/labels/bug",
            HttpStatusCode.NotFound, """{ "message": "Label does not exist" }""");
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/issues/3/labels",
            HttpStatusCode.OK, """[ { "name": "p1" } ]""");

        var result = await client.RemoveIssueLabelAsync("o", "r", "3", "bug");

        result.GetValueOrDefault().Should().BeEquivalentTo("p1");
    }

    [Test]
    public async Task CreateRelease_posts_payload_and_maps_release()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/releases",
            HttpStatusCode.Created,
            """{ "id": 55, "tag_name": "v1.0.0", "name": "v1.0.0", "html_url": "u", "draft": false, "prerelease": true }""");

        var result = await client.CreateReleaseAsync(
            new CreateReleaseRequest("o", "r", "v1.0.0", Prerelease: true, TargetCommitish: "main"));

        var release = result.Should().BeOfType<PlatformResult<Release>.Ok>().Subject.Value;
        release.Id.Should().Be("55");
        release.Prerelease.Should().BeTrue();
        handler.Requests.Single().Body.Should()
            .Contain("\"tag_name\":\"v1.0.0\"").And.Contain("\"target_commitish\":\"main\"");
    }

    [Test]
    public async Task CreateRelease_omits_target_commitish_when_unset()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/releases",
            HttpStatusCode.Created,
            """{ "id": 55, "tag_name": "v1.0.0", "name": "v1.0.0", "html_url": "u", "draft": false, "prerelease": false }""");

        await client.CreateReleaseAsync(new CreateReleaseRequest("o", "r", "v1.0.0"));

        handler.Requests.Single().Body.Should().NotContain("target_commitish");
    }

    [Test]
    public async Task ListPullRequestReviewComments_maps_anchors()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/pulls/7/comments",
            HttpStatusCode.OK,
            """
            [ { "id": 9, "body": "fix", "user": { "login": "rev" },
                "created_at": "2026-08-01T00:00:00Z", "path": "a.cs", "line": 12 } ]
            """);

        var result = await client.ListPullRequestReviewCommentsAsync("o", "r", "7");

        var comments = result.GetValueOrDefault()!;
        comments.Should().ContainSingle();
        comments[0].Path.Should().Be("a.cs");
        comments[0].Line.Should().Be(12);
    }

    [Test]
    public async Task ListCommits_maps_and_passes_since_filter()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/commits",
            HttpStatusCode.OK,
            """
            [ { "sha": "abc", "commit": { "message": "m", "author": { "name": "a", "date": "2026-08-01T00:00:00Z" } } } ]
            """);

        var result = await client.ListCommitsAsync(
            new ListCommitsRequest("o", "r", "main",
                Since: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));

        var commits = result.GetValueOrDefault()!;
        commits.Should().ContainSingle();
        commits[0].Sha.Should().Be("abc");
        handler.Requests.Single().Url.Should().Contain("sha=main").And.Contain("since=");
    }

    [Test]
    public async Task ListBranchFileChanges_uses_explicit_base_ref()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/compare/dev...feat",
            HttpStatusCode.OK,
            """{ "files": [ { "filename": "a.cs", "status": "modified", "additions": 1, "deletions": 2 } ] }""");

        var result = await client.ListBranchFileChangesAsync(
            new ListBranchFileChangesRequest("o", "r", "feat", BaseRef: "dev"));

        var files = result.GetValueOrDefault()!;
        files.Should().ContainSingle();
        files[0].Status.Should().Be(PrFileStatus.Modified);
    }

    [Test]
    public async Task ListBranchFileChanges_resolves_default_branch_when_base_omitted()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/compare/trunk...feat",
            HttpStatusCode.OK, """{ "files": [] }""");
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r",
            HttpStatusCode.OK, """{ "name": "r", "default_branch": "trunk" }""");

        var result = await client.ListBranchFileChangesAsync(
            new ListBranchFileChangesRequest("o", "r", "feat"));

        result.IsOk.Should().BeTrue();
        handler.Requests.Should().Contain(r => r.Url.Contains("/compare/trunk...feat"));
    }

    // ================================================================
    // Comments / webhooks / repo listing
    // ================================================================

    [Test]
    public async Task CreateIssueComment_posts_body()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/issues/7/comments",
            HttpStatusCode.Created,
            """{ "id": 2, "body": "hi", "user": { "login": "bot" }, "created_at": "2026-08-01T00:00:00Z" }""");

        var result = await client.CreateIssueCommentAsync("o", "r", "7", "hi");

        result.GetValueOrDefault()!.Body.Should().Be("hi");
    }

    [Test]
    public async Task CreatePullRequestComment_delegates_to_the_shared_issue_comment_surface()
    {
        // Epic 31 review (F-high) — the PR-comment verb exists because GitLab
        // MR iids are a separate sequence; GitHub issues and PRs share one
        // number space and one discussion-comment surface (/issues/{n}/comments).
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/issues/12/comments",
            HttpStatusCode.Created,
            """{ "id": 3, "body": "pr feedback", "user": { "login": "bot" }, "created_at": "2026-08-01T00:00:00Z" }""");

        var result = await client.CreatePullRequestCommentAsync("o", "r", "12", "pr feedback");

        result.GetValueOrDefault()!.Body.Should().Be("pr feedback");
        handler.Requests.Single().Url.Should().Contain("/issues/12/comments");
    }

    [Test]
    public async Task RegisterWebhook_posts_secret_config()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/hooks",
            HttpStatusCode.Created,
            """{ "id": 88, "active": true, "events": [ "push" ], "config": { "url": "https://t.example/hook" } }""");

        var result = await client.RegisterWebhookAsync(
            new RegisterWebhookRequest("o", "r", "https://t.example/hook", ["push"], "s3cret"));

        var hook = result.Should().BeOfType<PlatformResult<WebhookRegistration>.Ok>().Subject.Value;
        hook.Id.Should().Be("88");
        handler.Requests.Single().Body.Should()
            .Contain("\"secret\":\"s3cret\"").And.Contain("\"content_type\":\"json\"");
    }

    [Test]
    public async Task ListAccessibleRepos_pat_mode_pages_user_repos()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/user/repos?per_page=100&page=1",
            HttpStatusCode.OK,
            """[ { "name": "r1", "owner": { "login": "o" } }, { "name": "r2", "owner": { "login": "o" } } ]""");

        var repos = new List<Repo>();
        await foreach (var repo in client.ListAccessibleReposAsync())
        {
            repos.Add(repo);
        }

        repos.Select(r => r.Name).Should().BeEquivalentTo("r1", "r2");
    }

    [Test]
    public async Task ListAccessibleRepos_app_mode_pages_installation_repositories()
    {
        var (client, handler) = Build(appMode: true);
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/installation/repositories",
            HttpStatusCode.OK,
            """{ "total_count": 1, "repositories": [ { "name": "r1", "owner": { "login": "o" } } ] }""");

        var repos = new List<Repo>();
        await foreach (var repo in client.ListAccessibleReposAsync())
        {
            repos.Add(repo);
        }

        repos.Should().ContainSingle().Which.Name.Should().Be("r1");
    }

    /// <summary>
    /// THE vacuous-probe fix (execution plan P1 acceptance, red-first):
    /// a junk token against a 401-answering server must FAIL the
    /// enumeration — the old stub yield-broke, so any junk credential
    /// onboarded as "connected".
    /// </summary>
    [Test]
    public async Task ListAccessibleRepos_throws_on_bad_token_so_the_onboarding_probe_fails()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/user/repos",
            HttpStatusCode.Unauthorized, """{ "message": "Bad credentials" }""");

        var act = async () =>
        {
            await foreach (var _ in client.ListAccessibleReposAsync()) { }
        };

        var ex = await act.Should().ThrowAsync<GitHubPlatformApiException>();
        ex.Which.Error.Should().BeOfType<PlatformError.AuthExpired>();
    }

    // ================================================================
    // Error-classification parity (mediation-visible coarse classes)
    // ================================================================

    [Test]
    public async Task ErrorParity_404_maps_to_NotFound()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/pulls/7",
            HttpStatusCode.NotFound, """{ "message": "Not Found" }""");

        var result = await client.GetPullRequestAsync("o", "r", "7");

        result.Should().BeOfType<PlatformResult<PullRequest>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.NotFound>();
    }

    [Test]
    public async Task ErrorParity_403_with_exhausted_rate_limit_maps_to_RateLimited()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r", HttpStatusCode.Forbidden,
            """{ "message": "API rate limit exceeded" }""",
            new Dictionary<string, string>
            {
                ["X-RateLimit-Remaining"] = "0",
                ["X-RateLimit-Reset"] =
                    DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString(),
            });

        var result = await client.GetRepoAsync("o", "r");

        var err = result.Should().BeOfType<PlatformResult<Repo>.Failed>().Subject.Error;
        err.Should().BeOfType<PlatformError.RateLimited>()
            .Which.RetryAfter.Should().NotBeNull();
    }

    [Test]
    public async Task ErrorParity_plain_403_maps_to_PermissionDenied()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r", HttpStatusCode.Forbidden,
            """{ "message": "Resource not accessible by integration" }""");

        var result = await client.GetRepoAsync("o", "r");

        result.Should().BeOfType<PlatformResult<Repo>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.PermissionDenied>();
    }

    [Test]
    public async Task ErrorParity_422_maps_to_validation_failed()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/pulls?state=open",
            HttpStatusCode.OK, "[]");
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/pulls",
            HttpStatusCode.UnprocessableEntity, """{ "message": "Validation Failed" }""");

        var result = await client.OpenPullRequestAsync(
            new OpenPullRequestRequest("o", "r", "T", "feat", "main"));

        var err = result.Should().BeOfType<PlatformResult<PullRequest>.Failed>().Subject.Error;
        err.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("validation_failed");
    }

    [Test]
    public async Task ErrorParity_422_already_exists_maps_to_already_exists()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/git/refs",
            HttpStatusCode.UnprocessableEntity, """{ "message": "Reference already exists" }""");

        var result = await client.CreateBranchAsync(
            new CreateBranchRequest("o", "r", "feat", "sha"));

        var err = result.Should().BeOfType<PlatformResult<Branch>.Failed>().Subject.Error;
        err.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("already_exists");
    }

    [Test]
    public async Task ErrorParity_409_maps_to_conflict_class()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Put, $"{Api}/repos/o/r/pulls/7/merge",
            HttpStatusCode.Conflict, """{ "message": "Merge conflict" }""");

        var result = await client.MergePullRequestAsync(
            new MergePullRequestRequest("o", "r", "7", MergeMethod.Merge));

        var err = result.Should().BeOfType<PlatformResult<PullRequest>.Failed>().Subject.Error;
        err.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("merge_conflict");
    }

    [Test]
    public async Task ErrorParity_401_maps_to_AuthExpired()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r", HttpStatusCode.Unauthorized,
            """{ "message": "Bad credentials" }""");

        var result = await client.GetRepoAsync("o", "r");

        result.Should().BeOfType<PlatformResult<Repo>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.AuthExpired>();
    }

    [Test]
    public async Task ErrorParity_5xx_maps_to_ServiceUnavailable_error()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r",
            HttpStatusCode.BadGateway, "oops");

        var result = await client.GetRepoAsync("o", "r");

        result.Should().BeOfType<PlatformResult<Repo>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.ServiceUnavailable>();
    }

    // ================================================================
    // Argument validation
    // ================================================================

    [Test]
    public void Constructor_rejects_null_http_and_blank_host()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new GitHubHttpClient(
            new HttpClient(handler), Api, new GitHubAuth.Pat("t"));

        ((Action)(() => new GitHubPlatformClient(null!, "github.com")))
            .Should().Throw<ArgumentNullException>();
        ((Action)(() => new GitHubPlatformClient(http, "  ")))
            .Should().Throw<ArgumentException>();
    }
}
