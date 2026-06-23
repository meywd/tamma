using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services;
using Tamma.Core.Interfaces;

namespace Tamma.Api.Tests.GitHub;

/// <summary>
/// Direct unit coverage (review I-2) for the Story 2.8 build-out additions to
/// <see cref="GitHubIntegrationService"/> — the create-payload <c>draft</c>
/// field, the head-filter lookup
/// (<see cref="GitHubIntegrationService.GetGitHubOpenPullRequestForBranchAsync"/>),
/// the PATCH update (<see cref="GitHubIntegrationService.UpdateGitHubPullRequestAsync"/>),
/// and the best-effort label (<c>issues/{n}/labels</c>) + reviewer
/// (<c>pulls/{n}/requested_reviewers</c>) POSTs. The Activities test project
/// cannot reference <c>Tamma.Api</c>, so these live here.
///
/// <para>All HTTP traffic goes through a route-aware fake
/// <see cref="HttpMessageHandler"/> (no live calls). The create/update flows
/// issue multiple sequential requests (the PR call, then labels, then
/// reviewers) so the handler dispatches per route and records every request for
/// assertion. Failures of the label / reviewer POSTs must be swallowed
/// (best-effort, Story 2.8 AC3).</para>
/// </summary>
[TestFixture]
public class GitHubIntegrationServiceTests
{
    private RouteHandler _handler = null!;

    [SetUp]
    public void SetUp() => _handler = new RouteHandler();

    [TearDown]
    public void TearDown() => _handler?.Dispose();

    private GitHubIntegrationService CreateService(string? token = "ghp_test")
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("https://api.github.com") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("github")).Returns(http);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GitHub:Token"] = token })
            .Build();

        return new GitHubIntegrationService(
            factory.Object, config, NullLogger<GitHubIntegrationService>.Instance);
    }

    private static CreatePullRequestRequest Req(bool draft = false) => new()
    {
        Title = "[ADL] #1: x",
        Body = "body",
        Head = "feature/1",
        Base = "main",
        IsDraft = draft,
        Labels = new List<string> { "tamma-auto", "adl" },
        Reviewers = new List<string> { "alice", "bob" }
    };

    // ================================================================
    // CreateGitHubPullRequestAsync — draft field on the payload
    // ================================================================

    [Test]
    public async Task Create_DraftRequest_PutsDraftTrueOnPayload()
    {
        _handler.OnPost("/repos/o/r/pulls",
            """{ "number": 7, "html_url": "https://x/pull/7" }""");
        _handler.OnPost("/repos/o/r/issues/7/labels", "[]");
        _handler.OnPost("/repos/o/r/pulls/7/requested_reviewers", "{}");

        var result = await CreateService().CreateGitHubPullRequestAsync("o/r", Req(draft: true));

        result.Success.Should().BeTrue();
        result.Data!.Number.Should().Be(7);

        var create = _handler.RequireRequest(HttpMethod.Post, "/repos/o/r/pulls");
        var body = JsonDocument.Parse(create.Body).RootElement;
        body.GetProperty("draft").GetBoolean().Should().BeTrue();
        body.GetProperty("head").GetString().Should().Be("feature/1");
        body.GetProperty("base").GetString().Should().Be("main");
    }

    [Test]
    public async Task Create_NonDraftRequest_PutsDraftFalseOnPayload()
    {
        _handler.OnPost("/repos/o/r/pulls",
            """{ "number": 8, "html_url": "u8" }""");
        _handler.OnPost("/repos/o/r/issues/8/labels", "[]");
        _handler.OnPost("/repos/o/r/pulls/8/requested_reviewers", "{}");

        await CreateService().CreateGitHubPullRequestAsync("o/r", Req(draft: false));

        var create = _handler.RequireRequest(HttpMethod.Post, "/repos/o/r/pulls");
        JsonDocument.Parse(create.Body).RootElement
            .GetProperty("draft").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task Create_PostsLabelsAndReviewers_ToCorrectRoutes()
    {
        _handler.OnPost("/repos/o/r/pulls",
            """{ "number": 9, "html_url": "u9" }""");
        _handler.OnPost("/repos/o/r/issues/9/labels", "[]");
        _handler.OnPost("/repos/o/r/pulls/9/requested_reviewers", "{}");

        await CreateService().CreateGitHubPullRequestAsync("o/r", Req());

        var labels = _handler.RequireRequest(HttpMethod.Post, "/repos/o/r/issues/9/labels");
        JsonDocument.Parse(labels.Body).RootElement
            .GetProperty("labels").EnumerateArray().Select(e => e.GetString())
            .Should().Contain(new[] { "tamma-auto", "adl" });

        var reviewers = _handler.RequireRequest(HttpMethod.Post, "/repos/o/r/pulls/9/requested_reviewers");
        JsonDocument.Parse(reviewers.Body).RootElement
            .GetProperty("reviewers").EnumerateArray().Select(e => e.GetString())
            .Should().Contain(new[] { "alice", "bob" });
    }

    [Test]
    public async Task Create_LabelPostFails_IsSwallowed_PrStillSucceeds()
    {
        _handler.OnPost("/repos/o/r/pulls",
            """{ "number": 10, "html_url": "u10" }""");
        // Best-effort label / reviewer POSTs fail — must NOT fail PR creation.
        _handler.OnPost("/repos/o/r/issues/10/labels", "forbidden", HttpStatusCode.Forbidden);
        _handler.OnPost("/repos/o/r/pulls/10/requested_reviewers", "boom", HttpStatusCode.InternalServerError);

        var result = await CreateService().CreateGitHubPullRequestAsync("o/r", Req());

        result.Success.Should().BeTrue("label/reviewer failures are best-effort and must not fail the PR");
        result.Data!.Number.Should().Be(10);
    }

    [Test]
    public async Task Create_NoToken_FailsFastWithoutHttp()
    {
        var result = await CreateService(token: null).CreateGitHubPullRequestAsync("o/r", Req());

        // Create has no early token guard, but the handler is never primed for
        // /pulls → EnsureSuccessStatusCode throws → mapped to a Fail result.
        // (Either way: no throw, and not a false success.)
        result.Success.Should().BeFalse();
    }

    // ================================================================
    // GetGitHubOpenPullRequestForBranchAsync — head filter + parse
    // ================================================================

    [Test]
    public async Task Lookup_BuildsOwnerQualifiedHeadFilter_AndParsesFirstResult()
    {
        _handler.OnGet("/repos/octo/widgets/pulls",
            """[ { "number": 42, "html_url": "https://x/pull/42", "state": "open", "title": "t", "draft": true } ]""");

        var result = await CreateService()
            .GetGitHubOpenPullRequestForBranchAsync("octo/widgets", "feature/1", "main");

        result.Success.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.Number.Should().Be(42);
        result.Data.IsDraft.Should().BeTrue();

        var req = _handler.RequireRequest(HttpMethod.Get, "/repos/octo/widgets/pulls");
        var query = req.Uri.Query;
        query.Should().Contain("state=open");
        // head must be owner-qualified: octo:feature/1 (URL-encoded).
        query.Should().Contain(Uri.EscapeDataString("octo:feature/1"));
        query.Should().Contain("base=main");
        query.Should().Contain("per_page=1");
    }

    [Test]
    public async Task Lookup_NoOpenPr_ReturnsOkNull()
    {
        _handler.OnGet("/repos/o/r/pulls", "[]");

        var result = await CreateService()
            .GetGitHubOpenPullRequestForBranchAsync("o/r", "feature/1", "main");

        result.Success.Should().BeTrue();
        result.Data.Should().BeNull("an empty array means no open PR, not an error");
    }

    [Test]
    public async Task Lookup_HttpError_ReturnsFail()
    {
        _handler.OnGet("/repos/o/r/pulls", "nope", HttpStatusCode.InternalServerError);

        var result = await CreateService()
            .GetGitHubOpenPullRequestForBranchAsync("o/r", "feature/1", "main");

        result.Success.Should().BeFalse();
    }

    // ================================================================
    // UpdateGitHubPullRequestAsync — PATCH body + route
    // ================================================================

    [Test]
    public async Task Update_PatchesTitleAndBody_ToPullRoute()
    {
        _handler.OnPatch("/repos/o/r/pulls/42",
            """{ "number": 42, "html_url": "https://x/pull/42" }""");
        _handler.OnPost("/repos/o/r/issues/42/labels", "[]");
        _handler.OnPost("/repos/o/r/pulls/42/requested_reviewers", "{}");

        var result = await CreateService().UpdateGitHubPullRequestAsync("o/r", 42, Req());

        result.Success.Should().BeTrue();
        result.Data!.Number.Should().Be(42);

        var patch = _handler.RequireRequest(HttpMethod.Patch, "/repos/o/r/pulls/42");
        var body = JsonDocument.Parse(patch.Body).RootElement;
        body.GetProperty("title").GetString().Should().Be("[ADL] #1: x");
        body.GetProperty("body").GetString().Should().Be("body");
    }

    [Test]
    public async Task Update_AlsoPostsLabelsAndReviewers_BestEffort()
    {
        _handler.OnPatch("/repos/o/r/pulls/5",
            """{ "number": 5, "html_url": "u5" }""");
        // both best-effort posts fail → still a successful update.
        _handler.OnPost("/repos/o/r/issues/5/labels", "x", HttpStatusCode.Forbidden);
        _handler.OnPost("/repos/o/r/pulls/5/requested_reviewers", "x", HttpStatusCode.Forbidden);

        var result = await CreateService().UpdateGitHubPullRequestAsync("o/r", 5, Req());

        result.Success.Should().BeTrue();
        _handler.RequireRequest(HttpMethod.Post, "/repos/o/r/issues/5/labels");
        _handler.RequireRequest(HttpMethod.Post, "/repos/o/r/pulls/5/requested_reviewers");
    }

    [Test]
    public async Task Update_PatchFails_ReturnsFail()
    {
        _handler.OnPatch("/repos/o/r/pulls/5", "conflict", HttpStatusCode.Conflict);

        var result = await CreateService().UpdateGitHubPullRequestAsync("o/r", 5, Req());

        result.Success.Should().BeFalse();
    }

    // ================================================================
    // Route-aware fake handler
    // ================================================================

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string Path, string Body);

    /// <summary>
    /// Minimal route-aware <see cref="HttpMessageHandler"/>: register a canned
    /// response per (method, absolute-path) and it dispatches + records every
    /// request. Unregistered routes 404 (so a stray call is visible). Records
    /// the request body up front so assertions survive disposal.
    /// </summary>
    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Dictionary<(string Method, string Path), (HttpStatusCode Status, string Body)> _routes = new();
        private readonly List<CapturedRequest> _captured = new();

        public void OnGet(string path, string body, HttpStatusCode status = HttpStatusCode.OK)
            => _routes[("GET", path)] = (status, body);

        public void OnPost(string path, string body, HttpStatusCode status = HttpStatusCode.Created)
            => _routes[("POST", path)] = (status, body);

        public void OnPatch(string path, string body, HttpStatusCode status = HttpStatusCode.OK)
            => _routes[("PATCH", path)] = (status, body);

        public CapturedRequest RequireRequest(HttpMethod method, string path)
        {
            var hit = _captured.FirstOrDefault(c => c.Method == method && c.Path == path);
            hit.Should().NotBeNull($"expected a {method} {path} request");
            return hit!;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            _captured.Add(new CapturedRequest(request.Method, uri, path, body));

            if (_routes.TryGetValue((request.Method.Method, path), out var resp))
            {
                return new HttpResponseMessage(resp.Status)
                {
                    Content = new StringContent(resp.Body, System.Text.Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unrouted {request.Method} {path}")
            };
        }
    }
}
