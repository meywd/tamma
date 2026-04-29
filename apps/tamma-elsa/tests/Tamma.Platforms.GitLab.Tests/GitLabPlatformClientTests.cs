using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitLab;
using Tamma.Platforms.GitLab.Tests.Support;

namespace Tamma.Platforms.GitLab.Tests;

[TestFixture]
public sealed class GitLabPlatformClientTests
{
    [Test]
    public void EncodeProjectRef_url_encodes_slash()
    {
        // Numeric project id case - just owner/repo
        GitLabPlatformClient.EncodeProjectRef("group", "project").Should().Be("group%2Fproject");
    }

    [Test]
    public void EncodeProjectRef_handles_nested_groups()
    {
        // Nested-group path: owner = "group/subgroup", repo = "project"
        GitLabPlatformClient.EncodeProjectRef("group/subgroup", "project")
            .Should().Be("group%2Fsubgroup%2Fproject");
    }

    [Test]
    public async Task GetRepoAsync_happy_path_returns_Ok()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.AddRoute(HttpMethod.Get, "/projects/group%2Fproject", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """
                {
                  "id": 12,
                  "path_with_namespace": "group/project",
                  "default_branch": "main",
                  "visibility": "private",
                  "description": "demo",
                  "http_url_to_repo": "https://gitlab.example.com/group/project.git",
                  "web_url": "https://gitlab.example.com/group/project"
                }
                """));

        var result = await client.GetRepoAsync("group", "project");

        result.Should().BeOfType<PlatformResult<Repo>.Ok>();
        var repo = ((PlatformResult<Repo>.Ok)result).Value;
        repo.Owner.Should().Be("group");
        repo.Name.Should().Be("project");
        repo.DefaultBranch.Should().Be("main");
        repo.IsPrivate.Should().BeTrue();
        repo.Host.Should().Be("gitlab.example.com");
    }

    [Test]
    public async Task GetRepoAsync_404_maps_to_NotFound()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.AddRoute(HttpMethod.Get, "/projects/", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}"));

        var result = await client.GetRepoAsync("g", "missing");

        result.Should().BeOfType<PlatformResult<Repo>.Failed>();
        ((PlatformResult<Repo>.Failed)result).Error.Should().BeOfType<PlatformError.NotFound>();
    }

    [Test]
    public async Task GetRepoAsync_401_maps_to_AuthExpired()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.AddRoute(HttpMethod.Get, "/projects/", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Unauthorized, "{\"message\":\"401\"}"));

        var result = await client.GetRepoAsync("g", "p");
        ((PlatformResult<Repo>.Failed)result).Error.Should().BeOfType<PlatformError.AuthExpired>();
    }

    [Test]
    public async Task GetRepoAsync_429_maps_to_RateLimited_with_retryAfter()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.AddRoute(HttpMethod.Get, "/projects/", _ =>
        {
            var resp = FakeHttpMessageHandler.Json((HttpStatusCode)429, "{\"message\":\"rate limit\"}");
            resp.Headers.TryAddWithoutValidation("Retry-After", "30");
            return resp;
        });

        var result = await client.GetRepoAsync("g", "p");
        var failed = (PlatformResult<Repo>.Failed)result;
        var rl = failed.Error.Should().BeOfType<PlatformError.RateLimited>().Subject;
        rl.RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task ListRepoBranchesAsync_paginates_and_returns_all_branches()
    {
        var (client, handler) = TestFactory.BuildClient();
        // Page 1
        handler.EnqueueResponse(_ =>
        {
            var resp = FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """
                [
                  {"name":"main","protected":true,"commit":{"id":"abc"}},
                  {"name":"dev","protected":false,"commit":{"id":"def"}}
                ]
                """);
            resp.Headers.TryAddWithoutValidation(
                "Link",
                "<https://gitlab.example.com/api/v4/projects/g%2Fp/repository/branches?per_page=100&page=2>; rel=\"next\"");
            return resp;
        });
        // Page 2
        handler.EnqueueResponse(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """[{"name":"feature/x","protected":false,"commit":{"id":"123"}}]"""));

        var result = await client.ListRepoBranchesAsync("g", "p");

        result.Should().BeOfType<PlatformResult<IReadOnlyList<Branch>>.Ok>();
        var branches = ((PlatformResult<IReadOnlyList<Branch>>.Ok)result).Value;
        branches.Should().HaveCount(3);
        branches[0].Name.Should().Be("main");
        branches[0].Protected.Should().BeTrue();
        branches[2].Name.Should().Be("feature/x");
    }

    [Test]
    public async Task ListRepoBranchesAsync_403_maps_to_PermissionDenied()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.EnqueueResponse(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Forbidden, "{\"message\":\"403\"}"));

        var result = await client.ListRepoBranchesAsync("g", "p");
        ((PlatformResult<IReadOnlyList<Branch>>.Failed)result).Error
            .Should().BeOfType<PlatformError.PermissionDenied>();
    }

    [Test]
    public async Task GetFileContentAsync_decodes_base64()
    {
        var (client, handler) = TestFactory.BuildClient();
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hello world"));
        handler.AddRoute(HttpMethod.Get, "/repository/files/", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                $"{{\"file_name\":\"a.txt\",\"file_path\":\"a.txt\",\"encoding\":\"base64\",\"content\":\"{b64}\",\"ref\":\"main\"}}"));

        var result = await client.GetFileContentAsync(
            new GetFileContentRequest("g", "p", "a.txt", "main"));

        result.Should().BeOfType<PlatformResult<byte[]>.Ok>();
        var bytes = ((PlatformResult<byte[]>.Ok)result).Value;
        System.Text.Encoding.UTF8.GetString(bytes).Should().Be("hello world");
    }

    [Test]
    public async Task GetFileContentAsync_404_maps_to_NotFound()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.AddRoute(HttpMethod.Get, "/repository/files/", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.NotFound, "{\"message\":\"404\"}"));

        var result = await client.GetFileContentAsync(
            new GetFileContentRequest("g", "p", "missing.txt", "main"));
        ((PlatformResult<byte[]>.Failed)result).Error.Should().BeOfType<PlatformError.NotFound>();
    }

    [Test]
    public async Task CreateBranchAsync_happy_path()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.AddRoute(HttpMethod.Post, "/repository/branches", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Created,
                """{"name":"feat/new","protected":false,"commit":{"id":"abc123"}}"""));

        var result = await client.CreateBranchAsync(
            new CreateBranchRequest("g", "p", "feat/new", "abc123"));

        result.Should().BeOfType<PlatformResult<Branch>.Ok>();
        var branch = ((PlatformResult<Branch>.Ok)result).Value;
        branch.Name.Should().Be("feat/new");
        branch.Sha.Should().Be("abc123");
    }

    [Test]
    public async Task CreateBranchAsync_409_maps_to_InvalidRequest_conflict()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.AddRoute(HttpMethod.Post, "/repository/branches", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Conflict,
                "{\"message\":\"branch already exists\"}"));

        var result = await client.CreateBranchAsync(
            new CreateBranchRequest("g", "p", "feat/dup", "abc"));
        ((PlatformResult<Branch>.Failed)result).Error
            .Should().BeOfType<PlatformError.InvalidRequest>();
    }

    [Test]
    public async Task OpenPullRequestAsync_idempotent_returns_existing_MR()
    {
        var (client, handler) = TestFactory.BuildClient();
        // The idempotency lookup goes first — return an existing MR.
        handler.EnqueueResponse(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """
                [
                  {"id":1,"iid":7,"title":"Existing","source_branch":"feat","target_branch":"main","state":"opened","author":{"username":"alice"},"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-02T00:00:00Z"}
                ]
                """));

        var result = await client.OpenPullRequestAsync(
            new OpenPullRequestRequest("g", "p", "New", "feat", "main"));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
        var pr = ((PlatformResult<PullRequest>.Ok)result).Value;
        pr.Number.Should().Be("7");
        pr.Title.Should().Be("Existing");
        // Only one HTTP call should have been made (lookup, no create).
        handler.Requests.Should().HaveCount(1);
    }

    [Test]
    public async Task OpenPullRequestAsync_creates_when_no_existing_MR()
    {
        var (client, handler) = TestFactory.BuildClient();
        // Idempotency lookup returns empty list.
        handler.EnqueueResponse(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, "[]"));
        // Then the POST creates the MR.
        handler.EnqueueResponse(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Created,
                """{"id":2,"iid":8,"title":"New","source_branch":"feat","target_branch":"main","state":"opened","author":{"username":"bob"},"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z","web_url":"https://gitlab.example.com/g/p/-/merge_requests/8"}"""));

        var result = await client.OpenPullRequestAsync(
            new OpenPullRequestRequest("g", "p", "New", "feat", "main"));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
        var pr = ((PlatformResult<PullRequest>.Ok)result).Value;
        pr.Number.Should().Be("8");
        pr.HtmlUrl.Should().Contain("/merge_requests/8");
        handler.Requests.Should().HaveCount(2);
    }

    [Test]
    public async Task OpenPullRequestAsync_draft_prepends_Draft_prefix()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.EnqueueResponse(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, "[]"));
        string? capturedBody = null;
        handler.EnqueueResponse(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttpMessageHandler.Json(HttpStatusCode.Created,
                """{"iid":9,"title":"Draft: x","state":"opened","draft":true,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}""");
        });

        await client.OpenPullRequestAsync(
            new OpenPullRequestRequest("g", "p", "x", "feat", "main", IsDraft: true));

        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain("Draft: x");
    }

    [Test]
    public async Task GetPullRequestAsync_returns_mapped_PR()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.AddRoute(HttpMethod.Get, "/merge_requests/5", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"iid":5,"title":"Fix","state":"merged","draft":false,"work_in_progress":false,"author":{"username":"alice"},"source_branch":"f","target_branch":"main","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}"""));

        var result = await client.GetPullRequestAsync("g", "p", "5");

        var pr = ((PlatformResult<PullRequest>.Ok)result).Value;
        pr.State.Should().Be(PullRequestState.Merged);
        pr.Number.Should().Be("5");
    }

    [Test]
    public async Task ListPullRequestFilesAsync_returns_mapped_files()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.AddRoute(HttpMethod.Get, "/merge_requests/3/changes", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """
                {
                  "changes": [
                    {"old_path":"a.txt","new_path":"a.txt","diff":"+added\n","new_file":false,"deleted_file":false,"renamed_file":false},
                    {"old_path":"b.txt","new_path":"b.txt","diff":"-removed\n","new_file":false,"deleted_file":true,"renamed_file":false}
                  ]
                }
                """));

        var result = await client.ListPullRequestFilesAsync("g", "p", "3");

        result.Should().BeOfType<PlatformResult<IReadOnlyList<PrFile>>.Ok>();
        var files = ((PlatformResult<IReadOnlyList<PrFile>>.Ok)result).Value;
        files.Should().HaveCount(2);
        files[0].Status.Should().Be(PrFileStatus.Modified);
        files[1].Status.Should().Be(PrFileStatus.Removed);
    }

    [Test]
    public async Task CreatePullRequestReviewCommentAsync_returns_first_note()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.AddRoute(HttpMethod.Post, "/merge_requests/3/discussions", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Created,
                """{"id":"d-1","notes":[{"id":42,"body":"please fix","author":{"username":"reviewer"},"created_at":"2026-01-01T00:00:00Z"}]}"""));

        var result = await client.CreatePullRequestReviewCommentAsync(
            new CreatePullRequestReviewCommentRequest(
                "g", "p", "3", "src/foo.cs", 10, "please fix", "abc123"));

        result.Should().BeOfType<PlatformResult<IssueComment>.Ok>();
        var comment = ((PlatformResult<IssueComment>.Ok)result).Value;
        comment.Id.Should().Be("42");
        comment.AuthorLogin.Should().Be("reviewer");
    }

    [Test]
    public async Task MergePullRequestAsync_merge_method_succeeds()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.AddRoute(HttpMethod.Put, "/merge_requests/5/merge", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"iid":5,"title":"x","state":"merged","draft":false,"work_in_progress":false,"author":{"username":"u"},"source_branch":"s","target_branch":"t","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}"""));

        var result = await client.MergePullRequestAsync(
            new MergePullRequestRequest("g", "p", "5", MergeMethod.Merge));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
        ((PlatformResult<PullRequest>.Ok)result).Value.State.Should().Be(PullRequestState.Merged);
    }

    [Test]
    public async Task MergePullRequestAsync_rebase_method_returns_unsupported()
    {
        var (client, handler) = TestFactory.BuildClient();

        var result = await client.MergePullRequestAsync(
            new MergePullRequestRequest("g", "p", "5", MergeMethod.Rebase));

        var failed = (PlatformResult<PullRequest>.Failed)result;
        failed.Error.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("merge_method_unsupported");
        // No HTTP call should have happened.
        handler.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task CreateIssueCommentAsync_returns_note()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.AddRoute(HttpMethod.Post, "/issues/7/notes", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Created,
                """{"id":99,"body":"hello","author":{"username":"alice"},"created_at":"2026-01-01T00:00:00Z"}"""));

        var result = await client.CreateIssueCommentAsync("g", "p", "7", "hello");

        result.Should().BeOfType<PlatformResult<IssueComment>.Ok>();
        ((PlatformResult<IssueComment>.Ok)result).Value.Id.Should().Be("99");
    }

    [Test]
    public async Task RegisterWebhookAsync_maps_event_array_to_boolean_flags()
    {
        var (client, handler) = TestFactory.BuildClient();
        string? capturedBody = null;
        handler.AddRoute(HttpMethod.Post, "/hooks", req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttpMessageHandler.Json(HttpStatusCode.Created,
                """{"id":11,"url":"https://tamma.example.com/wh","push_events":true,"merge_requests_events":true,"issues_events":false,"pipeline_events":true,"enable_ssl_verification":true}""");
        });

        var result = await client.RegisterWebhookAsync(
            new RegisterWebhookRequest(
                "g", "p", "https://tamma.example.com/wh",
                new[] { "push", "merge_request", "pipeline" },
                "sekret"));

        result.Should().BeOfType<PlatformResult<WebhookRegistration>.Ok>();
        var reg = ((PlatformResult<WebhookRegistration>.Ok)result).Value;
        reg.Id.Should().Be("11");
        reg.Events.Should().Contain("push");
        reg.Events.Should().Contain("merge_request");
        reg.Events.Should().Contain("pipeline");
        reg.Events.Should().NotContain("issue");

        // Verify the request body uses the GitLab boolean flags.
        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain("\"push_events\":true");
        capturedBody.Should().Contain("\"merge_requests_events\":true");
        capturedBody.Should().Contain("\"issues_events\":false");
        capturedBody.Should().Contain("\"pipeline_events\":true");
        // Token is the static-token field for GitLab webhooks.
        capturedBody.Should().Contain("\"token\":\"sekret\"");
    }

    [Test]
    public async Task ListAccessibleReposAsync_paginates_via_link_header()
    {
        var (client, handler) = TestFactory.BuildClient();
        handler.EnqueueResponse(_ =>
        {
            var resp = FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """
                [
                  {"id":1,"path_with_namespace":"g/p1","default_branch":"main","visibility":"public","http_url_to_repo":"https://gitlab.example.com/g/p1.git","web_url":"https://gitlab.example.com/g/p1"}
                ]
                """);
            resp.Headers.TryAddWithoutValidation(
                "Link",
                "<https://gitlab.example.com/api/v4/projects?membership=true&simple=false&order_by=last_activity_at&per_page=100&page=2>; rel=\"next\"");
            return resp;
        });
        handler.EnqueueResponse(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """
                [
                  {"id":2,"path_with_namespace":"g/p2","default_branch":"main","visibility":"private","http_url_to_repo":"https://gitlab.example.com/g/p2.git","web_url":"https://gitlab.example.com/g/p2"}
                ]
                """));

        var repos = new List<Repo>();
        await foreach (var repo in client.ListAccessibleReposAsync())
        {
            repos.Add(repo);
        }

        repos.Should().HaveCount(2);
        repos[0].Name.Should().Be("p1");
        repos[1].IsPrivate.Should().BeTrue();
    }
}
