using System.Net;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Gitea.Tests;

/// <summary>
/// Unit tests covering every <see cref="IGitPlatformClient"/> method
/// — happy path + at least one error mapping per route per impl-plan
/// §6.
/// </summary>
[TestFixture]
public class GiteaPlatformClientTests
{
    // ───────────── GetRepoAsync ─────────────

    [Test]
    public async Task GetRepoAsync_ReturnsMappedRepo_OnSuccess()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo",
            HttpStatusCode.OK, """
            {
              "id": 1,
              "name": "repo",
              "full_name": "octo/repo",
              "owner": { "login": "octo", "id": 7 },
              "description": "demo",
              "private": false,
              "default_branch": "main",
              "clone_url": "https://gitea.example.com/octo/repo.git",
              "html_url": "https://gitea.example.com/octo/repo"
            }
            """);

        var result = await client.GetRepoAsync("octo", "repo");

        result.Should().BeOfType<PlatformResult<Repo>.Ok>();
        var repo = result.GetValueOrDefault()!;
        repo.Owner.Should().Be("octo");
        repo.Name.Should().Be("repo");
        repo.DefaultBranch.Should().Be("main");
        repo.IsPrivate.Should().BeFalse();
        repo.Host.Should().Be("gitea.example.com");
    }

    [Test]
    public async Task GetRepoAsync_MapsNotFound()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo",
            HttpStatusCode.NotFound,
            """{"message":"Not Found","url":"x"}""");

        var result = await client.GetRepoAsync("octo", "repo");

        result.Should().BeOfType<PlatformResult<Repo>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.NotFound>();
    }

    [Test]
    public async Task GetRepoAsync_MapsUnauthorized()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo",
            HttpStatusCode.Unauthorized, "{}");

        var result = await client.GetRepoAsync("octo", "repo");

        result.Should().BeOfType<PlatformResult<Repo>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.AuthExpired>();
    }

    [Test]
    public async Task GetRepoAsync_MapsRateLimit_WithRetryAfterHeader()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo",
            (HttpStatusCode)429, "{}",
            new Dictionary<string, string> { ["Retry-After"] = "60" });

        var result = await client.GetRepoAsync("octo", "repo");

        var failed = result.Should().BeOfType<PlatformResult<Repo>.Failed>().Subject;
        var rl = failed.Error.Should().BeOfType<PlatformError.RateLimited>().Subject;
        rl.RetryAfter.Should().Be(TimeSpan.FromSeconds(60));
    }

    // ───────────── ListRepoBranchesAsync ─────────────

    [Test]
    public async Task ListRepoBranchesAsync_AggregatesAcrossPages()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        // Page 1: 50 entries triggers another page request.
        var page1 = "[" + string.Join(",", Enumerable.Range(1, 50).Select(i =>
            $$"""{"name":"branch-{{i}}","commit":{"id":"sha{{i}}"},"protected":false}""")) + "]";
        // Page 2: short page → terminate.
        var page2 = """[{"name":"branch-51","commit":{"id":"sha51"},"protected":true}]""";
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/branches?page=1",
            HttpStatusCode.OK, page1);
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/branches?page=2",
            HttpStatusCode.OK, page2);

        var result = await client.ListRepoBranchesAsync("octo", "repo");

        var branches = result.Should()
            .BeOfType<PlatformResult<IReadOnlyList<Branch>>.Ok>()
            .Subject.Value;
        branches.Should().HaveCount(51);
        branches[^1].Name.Should().Be("branch-51");
        branches[^1].Protected.Should().BeTrue();
    }

    [Test]
    public async Task ListRepoBranchesAsync_MapsErrorMidPagination()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/branches?page=1",
            HttpStatusCode.Forbidden, "{}");

        var result = await client.ListRepoBranchesAsync("octo", "repo");

        result.Should().BeOfType<PlatformResult<IReadOnlyList<Branch>>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.PermissionDenied>();
    }

    // ───────────── GetFileContentAsync ─────────────

    [Test]
    public async Task GetFileContentAsync_DecodesBase64()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello tamma"));
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/contents/README.md",
            HttpStatusCode.OK,
            $$"""{"type":"file","encoding":"base64","content":"{{encoded}}","size":11}""");

        var result = await client.GetFileContentAsync(
            new GetFileContentRequest("octo", "repo", "README.md", "main"));

        result.Should().BeOfType<PlatformResult<byte[]>.Ok>();
        Encoding.UTF8.GetString(result.GetValueOrDefault()!).Should().Be("hello tamma");
    }

    [Test]
    public async Task GetFileContentAsync_ReturnsEmpty_OnDirectoryType()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/contents/dir",
            HttpStatusCode.OK,
            """{"type":"dir","encoding":null,"content":null,"size":0}""");

        var result = await client.GetFileContentAsync(
            new GetFileContentRequest("octo", "repo", "dir", "main"));

        result.Should().BeOfType<PlatformResult<byte[]>.Ok>()
            .Which.Value.Should().BeEmpty();
    }

    // ───────────── CreateBranchAsync ─────────────

    [Test]
    public async Task CreateBranchAsync_PostsExpectedBody()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/octo/repo/branches",
            HttpStatusCode.Created,
            """{"name":"feat/x","commit":{"id":"sha-feat"},"protected":false}""");

        var result = await client.CreateBranchAsync(
            new CreateBranchRequest("octo", "repo", "feat/x", "sha-feat"));

        result.Should().BeOfType<PlatformResult<Branch>.Ok>()
            .Which.Value.Name.Should().Be("feat/x");
        var posted = handler.Requests.First(r => r.Method == HttpMethod.Post);
        posted.Body.Should().Contain("\"new_branch_name\":\"feat/x\"")
            .And.Contain("\"old_ref_name\":\"sha-feat\"");
    }

    [Test]
    public async Task CreateBranchAsync_MapsValidationFailure()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/octo/repo/branches",
            (HttpStatusCode)422,
            """{"message":"branch already exists"}""");

        var result = await client.CreateBranchAsync(
            new CreateBranchRequest("octo", "repo", "feat/x", "sha"));

        var ir = result.Should().BeOfType<PlatformResult<Branch>.Failed>()
            .Subject.Error.Should().BeOfType<PlatformError.InvalidRequest>().Subject;
        ir.Code.Should().Be("already_exists");
        ir.Hint.Should().Be("branch already exists");
    }

    // ───────────── OpenPullRequestAsync ─────────────

    [Test]
    public async Task OpenPullRequestAsync_ReturnsExisting_WhenIdempotentMatchFound()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        // List-open-PRs probe returns an existing match.
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls?state=open",
            HttpStatusCode.OK,
            """
            [{"number":42,"title":"existing","body":null,"state":"open",
              "merged":false,"draft":false,"html_url":"https://x",
              "user":{"login":"alice"},
              "head":{"ref":"feat/x"},"base":{"ref":"main"},
              "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z"}]
            """);

        var result = await client.OpenPullRequestAsync(new OpenPullRequestRequest(
            "octo", "repo", "new title", "feat/x", "main"));

        var pr = result.Should().BeOfType<PlatformResult<PullRequest>.Ok>().Subject.Value;
        pr.Number.Should().Be("42");
        pr.Title.Should().Be("existing");
        // No POST should have been issued.
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Test]
    public async Task OpenPullRequestAsync_CreatesNewPR_WhenNoExistingMatch()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls?state=open",
            HttpStatusCode.OK, "[]");
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls",
            HttpStatusCode.Created,
            """
            {"number":99,"title":"new","body":"b","state":"open",
              "merged":false,"draft":true,"html_url":"https://x",
              "user":{"login":"bot"},
              "head":{"ref":"feat/y"},"base":{"ref":"main"},
              "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z"}
            """);

        var result = await client.OpenPullRequestAsync(new OpenPullRequestRequest(
            "octo", "repo", "new", "feat/y", "main", Body: "b", IsDraft: true));

        var pr = result.Should().BeOfType<PlatformResult<PullRequest>.Ok>().Subject.Value;
        pr.Number.Should().Be("99");
        pr.IsDraft.Should().BeTrue();
        var posted = handler.Requests.First(r => r.Method == HttpMethod.Post);
        // P5 M1 — Gitea has no create-side draft field (ignored server-side);
        // draft IS the WIP title prefix, so a draft open prefixes the title.
        posted.Body.Should().Contain("\"title\":\"WIP: new\"")
            .And.Contain("\"head\":\"feat/y\"")
            .And.Contain("\"base\":\"main\"")
            .And.Contain("\"draft\":true");
    }

    [Test]
    public async Task OpenPullRequestAsync_DoesNotPrefixTitle_WhenNotDraft()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls?state=open",
            HttpStatusCode.OK, "[]");
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls",
            HttpStatusCode.Created,
            """
            {"number":100,"title":"plain","body":null,"state":"open",
              "merged":false,"draft":false,"html_url":"https://x",
              "user":{"login":"bot"},
              "head":{"ref":"feat/z"},"base":{"ref":"main"},
              "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z"}
            """);

        await client.OpenPullRequestAsync(new OpenPullRequestRequest(
            "octo", "repo", "plain", "feat/z", "main", IsDraft: false));

        var posted = handler.Requests.First(r => r.Method == HttpMethod.Post);
        posted.Body.Should().Contain("\"title\":\"plain\"");
    }

    // ───────────── GetPullRequestAsync ─────────────

    [Test]
    public async Task GetPullRequestAsync_MapsMergedFlagToMergedState()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/7",
            HttpStatusCode.OK,
            """
            {"number":7,"title":"x","body":null,"state":"closed","merged":true,
              "draft":false,"html_url":"https://x","user":{"login":"a"},
              "head":{"ref":"feat"},"base":{"ref":"main"},
              "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z"}
            """);

        var result = await client.GetPullRequestAsync("octo", "repo", "7");

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.State.Should().Be(PullRequestState.Merged);
    }

    // ───────────── ListPullRequestFilesAsync ─────────────

    [Test]
    public async Task ListPullRequestFilesAsync_MapsStatusValues()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/3/files?page=1",
            HttpStatusCode.OK,
            """
            [
              {"filename":"a.cs","status":"added","additions":10,"deletions":0},
              {"filename":"b.cs","status":"modified","additions":2,"deletions":3},
              {"filename":"c.cs","status":"removed","additions":0,"deletions":7},
              {"filename":"d.cs","status":"unknown_value","additions":1,"deletions":1}
            ]
            """);

        var result = await client.ListPullRequestFilesAsync("octo", "repo", "3");

        var files = result.Should()
            .BeOfType<PlatformResult<IReadOnlyList<PrFile>>.Ok>().Subject.Value;
        files.Should().HaveCount(4);
        files[0].Status.Should().Be(PrFileStatus.Added);
        files[1].Status.Should().Be(PrFileStatus.Modified);
        files[2].Status.Should().Be(PrFileStatus.Removed);
        files[3].Status.Should().Be(PrFileStatus.Other);
    }

    // ───────────── CreatePullRequestReviewCommentAsync ─────────────

    [Test]
    public async Task CreatePullRequestReviewCommentAsync_MapsResponse()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/3/reviews",
            HttpStatusCode.OK,
            """
            {"id":501,"body":"nit","state":"COMMENTED","user":{"login":"reviewer"},
              "submitted_at":"2026-04-21T00:00:00Z"}
            """);

        var result = await client.CreatePullRequestReviewCommentAsync(
            new CreatePullRequestReviewCommentRequest(
                "octo", "repo", "3", "src/x.cs", 12, "nit", "sha"));

        var comment = result.Should()
            .BeOfType<PlatformResult<IssueComment>.Ok>().Subject.Value;
        comment.Id.Should().Be("501");
        comment.AuthorLogin.Should().Be("reviewer");
    }

    // ───────────── MergePullRequestAsync ─────────────

    [Test]
    public async Task MergePullRequestAsync_PostsThenRefetchesPR()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/3/merge",
            HttpStatusCode.OK, "");
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/3",
            HttpStatusCode.OK,
            """
            {"number":3,"title":"x","body":null,"state":"closed","merged":true,
              "draft":false,"html_url":"https://x","user":{"login":"a"},
              "head":{"ref":"feat"},"base":{"ref":"main"},
              "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z"}
            """);

        var result = await client.MergePullRequestAsync(new MergePullRequestRequest(
            "octo", "repo", "3", MergeMethod.Squash, "merge msg"));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.State.Should().Be(PullRequestState.Merged);
        var post = handler.Requests.First(r => r.Method == HttpMethod.Post);
        post.Body.Should().Contain("\"Do\":\"squash\"");
    }

    [Test]
    public async Task MergePullRequestAsync_MapsConflict()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/3/merge",
            HttpStatusCode.Conflict,
            """{"message":"merge conflict detected"}""");

        var result = await client.MergePullRequestAsync(new MergePullRequestRequest(
            "octo", "repo", "3", MergeMethod.Merge));

        var ir = result.Should().BeOfType<PlatformResult<PullRequest>.Failed>()
            .Subject.Error.Should().BeOfType<PlatformError.InvalidRequest>().Subject;
        ir.Code.Should().Be("merge_conflict");
    }

    // ───────────── CreateIssueCommentAsync ─────────────

    [Test]
    public async Task CreateIssueCommentAsync_PostsBodyAndMapsResponse()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/octo/repo/issues/9/comments",
            HttpStatusCode.Created,
            """
            {"id":1234,"body":"comment","user":{"login":"bot"},
              "created_at":"2026-04-21T00:00:00Z"}
            """);

        var result = await client.CreateIssueCommentAsync("octo", "repo", "9", "comment");

        result.Should().BeOfType<PlatformResult<IssueComment>.Ok>()
            .Which.Value.Id.Should().Be("1234");
        var post = handler.Requests.First(r => r.Method == HttpMethod.Post);
        post.Body.Should().Contain("\"body\":\"comment\"");
    }

    // ───────────── RegisterWebhookAsync ─────────────

    [Test]
    public async Task RegisterWebhookAsync_PostsHookConfigAndMapsResponse()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/octo/repo/hooks",
            HttpStatusCode.Created,
            """
            {"id":7,"type":"gitea","config":{"url":"https://hook"},
              "events":["push"],"active":true}
            """);

        var result = await client.RegisterWebhookAsync(new RegisterWebhookRequest(
            "octo", "repo", "https://hook",
            new[] { "push" }, "secret-value"));

        result.Should().BeOfType<PlatformResult<WebhookRegistration>.Ok>()
            .Which.Value.Id.Should().Be("7");
        var post = handler.Requests.First(r => r.Method == HttpMethod.Post);
        post.Body.Should().Contain("\"url\":\"https://hook\"")
            .And.Contain("\"secret\":\"secret-value\"")
            .And.Contain("\"content_type\":\"json\"")
            .And.Contain("\"type\":\"gitea\"");
    }

    // ───────────── ListAccessibleReposAsync ─────────────

    [Test]
    public async Task ListAccessibleReposAsync_AggregatesPagesUntilShort()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        var page1 = "[" + string.Join(",", Enumerable.Range(1, 50).Select(i =>
            $$"""{"id":{{i}},"name":"r{{i}}","owner":{"login":"o"},"default_branch":"main","clone_url":"https://x","html_url":"https://x"}""")) + "]";
        var page2 = """[{"id":51,"name":"r51","owner":{"login":"o"},"default_branch":"main","clone_url":"https://x","html_url":"https://x"}]""";
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/user/repos?page=1",
            HttpStatusCode.OK, page1);
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/user/repos?page=2",
            HttpStatusCode.OK, page2);

        var collected = new List<Repo>();
        await foreach (var r in client.ListAccessibleReposAsync())
        {
            collected.Add(r);
        }

        collected.Should().HaveCount(51);
        collected[0].Name.Should().Be("r1");
        collected[^1].Name.Should().Be("r51");
    }

    [Test]
    public async Task ListAccessibleReposAsync_ThrowsTyped_OnPlatformRejection()
    {
        // P5 M1 probe strictness: a 403/401 must THROW typed, never complete
        // as a silent empty enumeration — otherwise a junk credential passes
        // the onboarding auth probe (the vacuous-probe class P1 closed for
        // GitHub).
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/user/repos?page=1",
            HttpStatusCode.Forbidden, "{}");

        var act = async () =>
        {
            await foreach (var _ in client.ListAccessibleReposAsync()) { }
        };

        (await act.Should().ThrowAsync<GiteaPlatformApiException>())
            .Which.Error.Should().BeOfType<PlatformError.PermissionDenied>();
    }

    [Test]
    public async Task ListAccessibleReposAsync_ThrowsTyped_OnUnauthorized()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/user/repos?page=1",
            HttpStatusCode.Unauthorized, "{}");

        var act = async () =>
        {
            await foreach (var _ in client.ListAccessibleReposAsync()) { }
        };

        (await act.Should().ThrowAsync<GiteaPlatformApiException>())
            .Which.Error.Should().BeOfType<PlatformError.AuthExpired>();
    }
}
