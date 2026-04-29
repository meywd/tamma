using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Gitea.Tests;

/// <summary>
/// Story 31-5 — contract test for the composed Forgejo driver. Drives
/// the Forgejo factory end-to-end via a scripted
/// <see cref="FakeHttpMessageHandler"/> at <c>forgejo.example.org</c>,
/// then exercises a representative method from each of the 12
/// <see cref="IGitPlatformClient"/> + 5
/// <see cref="IGitPlatformActionsClient"/> contracts. The same client
/// implementation (<see cref="GiteaPlatformClient"/>) backs both
/// Gitea and Forgejo drivers — this test asserts the wrapper's
/// delegation works against a Forgejo-host URL + Forgejo version.
///
/// <para>Per impl-plan §6, the integration-level contract suite (real
/// Forgejo container) lives in <c>Tamma.Platforms.IntegrationTests</c>
/// and runs nightly through the 31-10 harness. This unit-level
/// contract test catches delegation-level breakage at PR time without
/// the container cost.</para>
/// </summary>
[TestFixture]
public class ForgejoContractTests
{
    private const string ForgejoBaseUrl = "https://forgejo.example.org";
    private const string ForgejoHost = "forgejo.example.org";

    private static async Task<IGitPlatformDriver> BuildDriverAsync(
        FakeHttpMessageHandler handler)
    {
        // Factory probes /api/v1/version on first build — script the
        // Forgejo-suffixed response so the driver lands with full
        // capabilities.
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/version",
            HttpStatusCode.OK, """{"version":"1.21.5+forgejo-3"}""");

        var services = new ServiceCollection();
        services.AddHttpClient(ForgejoPlatformDriverFactory.ForgejoHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddForgejoPlatformDriver();
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredKeyedService<IGitPlatformDriverFactory>(
            PlatformKind.Forgejo);

        return await factory.CreateAsync(
            new PlatformInstallation(
                Id: Guid.NewGuid(),
                TenantId: Guid.NewGuid(),
                Kind: PlatformKind.Forgejo,
                BaseUrl: ForgejoBaseUrl,
                InstallationExternalId: null),
            credentialPlaintext: "ghs_test_token",
            default);
    }

    // ───── IGitPlatformClient surface (smoke through composition) ─────

    [Test]
    public async Task Forgejo_GetRepoAsync_DelegatesToGiteaClient()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo",
            HttpStatusCode.OK, """
            {
              "id": 1,
              "name": "repo",
              "full_name": "octo/repo",
              "owner": { "login": "octo", "id": 7 },
              "default_branch": "main",
              "private": false,
              "clone_url": "https://forgejo.example.org/octo/repo.git",
              "html_url": "https://forgejo.example.org/octo/repo"
            }
            """);
        var driver = await BuildDriverAsync(handler);

        var result = await driver.Client.GetRepoAsync("octo", "repo");

        var repo = result.Should().BeOfType<PlatformResult<Repo>.Ok>().Subject.Value;
        repo.Owner.Should().Be("octo");
        repo.Name.Should().Be("repo");
        repo.DefaultBranch.Should().Be("main");
        repo.Host.Should().Be(ForgejoHost);
    }

    [Test]
    public async Task Forgejo_ListRepoBranchesAsync_DelegatesAndPaginates()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/branches?page=1",
            HttpStatusCode.OK,
            """[{"name":"main","commit":{"id":"abc"},"protected":false}]""");
        var driver = await BuildDriverAsync(handler);

        var result = await driver.Client.ListRepoBranchesAsync("octo", "repo");

        var branches = result.Should()
            .BeOfType<PlatformResult<IReadOnlyList<Branch>>.Ok>()
            .Subject.Value;
        branches.Should().HaveCount(1);
        branches[0].Name.Should().Be("main");
    }

    [Test]
    public async Task Forgejo_GetFileContentAsync_DelegatesAndDecodes()
    {
        var handler = new FakeHttpMessageHandler();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello forgejo"));
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/contents/README.md",
            HttpStatusCode.OK,
            $$"""{"type":"file","encoding":"base64","content":"{{encoded}}","size":13}""");
        var driver = await BuildDriverAsync(handler);

        var result = await driver.Client.GetFileContentAsync(
            new GetFileContentRequest("octo", "repo", "README.md", "main"));

        result.Should().BeOfType<PlatformResult<byte[]>.Ok>();
        Encoding.UTF8.GetString(result.GetValueOrDefault()!)
            .Should().Be("hello forgejo");
    }

    [Test]
    public async Task Forgejo_CreateBranchAsync_DelegatesPostBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Post,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/branches",
            HttpStatusCode.Created,
            """{"name":"feat/x","commit":{"id":"sha-feat"},"protected":false}""");
        var driver = await BuildDriverAsync(handler);

        var result = await driver.Client.CreateBranchAsync(
            new CreateBranchRequest("octo", "repo", "feat/x", "sha-feat"));

        result.Should().BeOfType<PlatformResult<Branch>.Ok>()
            .Which.Value.Name.Should().Be("feat/x");
        var posted = handler.Requests.First(r => r.Method == HttpMethod.Post);
        posted.Body.Should().Contain("\"new_branch_name\":\"feat/x\"")
            .And.Contain("\"old_ref_name\":\"sha-feat\"");
    }

    [Test]
    public async Task Forgejo_OpenPullRequestAsync_DelegatesAndRunsIdempotency()
    {
        var handler = new FakeHttpMessageHandler();
        // Idempotency probe — no existing PR.
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/pulls?state=open",
            HttpStatusCode.OK, "[]");
        // POST /pulls returns the new PR.
        handler.EnqueueJson(HttpMethod.Post,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/pulls",
            HttpStatusCode.Created,
            """
            {"number":7,"title":"feat","body":null,"state":"open",
             "merged":false,"draft":false,"html_url":"https://x",
             "user":{"login":"alice"},
             "head":{"ref":"feat/x"},"base":{"ref":"main"},
             "created_at":"2026-04-27T00:00:00Z","updated_at":"2026-04-27T00:00:00Z"}
            """);
        var driver = await BuildDriverAsync(handler);

        var result = await driver.Client.OpenPullRequestAsync(
            new OpenPullRequestRequest("octo", "repo", "feat", "feat/x", "main"));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.Number.Should().Be("7");
    }

    [Test]
    public async Task Forgejo_GetPullRequestAsync_Delegates()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/pulls/7",
            HttpStatusCode.OK,
            """
            {"number":7,"title":"feat","body":"b","state":"open",
             "merged":false,"draft":false,"html_url":"https://x",
             "user":{"login":"alice"},
             "head":{"ref":"feat/x"},"base":{"ref":"main"},
             "created_at":"2026-04-27T00:00:00Z","updated_at":"2026-04-27T00:00:00Z"}
            """);
        var driver = await BuildDriverAsync(handler);

        var result = await driver.Client.GetPullRequestAsync("octo", "repo", "7");

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.Title.Should().Be("feat");
    }

    [Test]
    public async Task Forgejo_ListPullRequestFilesAsync_Delegates()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/pulls/7/files",
            HttpStatusCode.OK,
            """[{"filename":"a.cs","status":"modified","additions":3,"deletions":1,"changes":4,"sha":"f-sha","patch":"@@"}]""");
        var driver = await BuildDriverAsync(handler);

        var result = await driver.Client.ListPullRequestFilesAsync("octo", "repo", "7");

        result.Should()
            .BeOfType<PlatformResult<IReadOnlyList<PrFile>>.Ok>()
            .Which.Value.Should().HaveCount(1);
    }

    [Test]
    public async Task Forgejo_CreatePullRequestReviewCommentAsync_Delegates()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Post,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/pulls/7/reviews",
            HttpStatusCode.Created,
            """
            {"id":99,"body":"nit","html_url":"https://x",
             "user":{"login":"bot"},
             "created_at":"2026-04-27T00:00:00Z"}
            """);
        var driver = await BuildDriverAsync(handler);

        var result = await driver.Client.CreatePullRequestReviewCommentAsync(
            new CreatePullRequestReviewCommentRequest(
                Owner: "octo",
                RepoName: "repo",
                PrNumber: "7",
                Path: "a.cs",
                Line: 3,
                Body: "nit",
                CommitSha: "abc"));

        result.Should().BeOfType<PlatformResult<IssueComment>.Ok>()
            .Which.Value.Body.Should().Be("nit");
    }

    [Test]
    public async Task Forgejo_MergePullRequestAsync_Delegates()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Post,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/pulls/7/merge",
            HttpStatusCode.OK, "");
        // After merge, driver re-fetches the PR.
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/pulls/7",
            HttpStatusCode.OK,
            """
            {"number":7,"title":"feat","body":null,"state":"closed",
             "merged":true,"draft":false,"html_url":"https://x",
             "user":{"login":"alice"},
             "head":{"ref":"feat/x"},"base":{"ref":"main"},
             "created_at":"2026-04-27T00:00:00Z","updated_at":"2026-04-27T00:00:00Z"}
            """);
        var driver = await BuildDriverAsync(handler);

        var result = await driver.Client.MergePullRequestAsync(
            new MergePullRequestRequest("octo", "repo", "7", MergeMethod.Merge));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.State.Should().Be(PullRequestState.Merged);
    }

    [Test]
    public async Task Forgejo_CreateIssueCommentAsync_Delegates()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Post,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/issues/7/comments",
            HttpStatusCode.Created,
            """
            {"id":42,"body":"hi","user":{"login":"bot"},
             "created_at":"2026-04-27T00:00:00Z"}
            """);
        var driver = await BuildDriverAsync(handler);

        var result = await driver.Client.CreateIssueCommentAsync(
            "octo", "repo", "7", "hi");

        result.Should().BeOfType<PlatformResult<IssueComment>.Ok>()
            .Which.Value.Body.Should().Be("hi");
    }

    [Test]
    public async Task Forgejo_RegisterWebhookAsync_Delegates()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Post,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/hooks",
            HttpStatusCode.Created,
            """{"id":17,"type":"gitea","active":true,"events":["push"],"config":{"url":"https://hook"}}""");
        var driver = await BuildDriverAsync(handler);

        var result = await driver.Client.RegisterWebhookAsync(
            new RegisterWebhookRequest(
                Owner: "octo",
                RepoName: "repo",
                DeliveryUrl: "https://hook",
                Events: new[] { "push" },
                Secret: "s"));

        result.Should().BeOfType<PlatformResult<WebhookRegistration>.Ok>()
            .Which.Value.Id.Should().Be("17");
    }

    [Test]
    public async Task Forgejo_ListAccessibleReposAsync_Delegates()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/user/repos?page=1",
            HttpStatusCode.OK,
            """
            [
              {"id":1,"name":"r1","full_name":"o/r1","owner":{"login":"o","id":1},
               "default_branch":"main","private":false,
               "clone_url":"https://forgejo.example.org/o/r1.git",
               "html_url":"https://forgejo.example.org/o/r1"}
            ]
            """);
        var driver = await BuildDriverAsync(handler);

        var collected = new List<Repo>();
        await foreach (var r in driver.Client.ListAccessibleReposAsync())
        {
            collected.Add(r);
        }

        collected.Should().HaveCount(1);
        collected[0].Name.Should().Be("r1");
        collected[0].Host.Should().Be(ForgejoHost);
    }

    // ───── IGitPlatformActionsClient surface (5 methods) ─────

    [Test]
    public async Task Forgejo_DispatchWorkflowAsync_Delegates()
    {
        var handler = new FakeHttpMessageHandler();
        // Dispatch returns 204; driver then probes runs to find the
        // freshly-queued one.
        handler.EnqueueJson(HttpMethod.Post,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/actions/workflows/ci.yml/dispatches",
            HttpStatusCode.NoContent, "");
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/actions/runs?event=workflow_dispatch",
            HttpStatusCode.OK,
            """
            {"total_count":1,"workflow_runs":[{
              "id":123,"status":"queued","conclusion":null,
              "html_url":"https://x","started_at":"2026-04-27T00:00:00Z"}]}
            """);
        var driver = await BuildDriverAsync(handler);

        driver.Actions.Should().NotBeNull();
        var result = await driver.Actions!.DispatchWorkflowAsync(
            "octo", "repo",
            new WorkflowDispatchRequest(
                Ref: "main",
                WorkflowFileName: "ci.yml",
                Inputs: new Dictionary<string, string>()));

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Ok>()
            .Which.Value.RunId.Should().Be("123");
    }

    [Test]
    public async Task Forgejo_GetRunStatusAsync_Delegates()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/actions/runs/123",
            HttpStatusCode.OK,
            """
            {"id":123,"status":"completed","conclusion":"success",
             "html_url":"https://x","started_at":"2026-04-27T00:00:00Z",
             "completed_at":"2026-04-27T00:05:00Z"}
            """);
        var driver = await BuildDriverAsync(handler);

        var result = await driver.Actions!.GetRunStatusAsync("octo", "repo", "123");

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Ok>()
            .Which.Value.Status.Should().Be("completed");
    }

    [Test]
    public async Task Forgejo_ListRunJobsAsync_Delegates()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/actions/runs/123/jobs",
            HttpStatusCode.OK,
            """{"total_count":1,"jobs":[{"id":1,"name":"build","status":"completed","conclusion":"success"}]}""");
        var driver = await BuildDriverAsync(handler);

        var result = await driver.Actions!.ListRunJobsAsync("octo", "repo", "123");

        result.Should().BeOfType<PlatformResult<IReadOnlyList<WorkflowJob>>.Ok>()
            .Which.Value.Should().HaveCount(1);
    }

    [Test]
    public async Task Forgejo_DownloadArtifactAsync_DelegatesWithCap()
    {
        var handler = new FakeHttpMessageHandler();
        var bytes = Encoding.UTF8.GetBytes("artifact-bytes");
        handler.Enqueue(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/actions/artifacts/9/zip",
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            });
        var driver = await BuildDriverAsync(handler);

        var result = await driver.Actions!.DownloadArtifactAsync(
            "octo", "repo", "9");

        result.Should().BeOfType<PlatformResult<Stream>.Ok>();
        using var stream = result.GetValueOrDefault()!;
        var buffer = new byte[64];
        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
        read.Should().Be(bytes.Length);
        Encoding.UTF8.GetString(buffer, 0, read).Should().Be("artifact-bytes");
    }

    [Test]
    public async Task Forgejo_CancelRunAsync_Delegates()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Post,
            $"{ForgejoBaseUrl}/api/v1/repos/octo/repo/actions/runs/123/cancel",
            HttpStatusCode.Accepted, "{}");
        var driver = await BuildDriverAsync(handler);

        var result = await driver.Actions!.CancelRunAsync("octo", "repo", "123");

        result.Should().BeOfType<PlatformResult<bool>.Ok>()
            .Which.Value.Should().BeTrue();
    }
}
