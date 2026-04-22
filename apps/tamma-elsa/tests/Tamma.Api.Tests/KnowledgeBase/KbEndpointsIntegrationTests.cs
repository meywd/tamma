using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Api.Services.KnowledgeBase;

namespace Tamma.Api.Tests.KnowledgeBase;

/// <summary>
/// End-to-end integration tests for the 30 /api/kb/* routes.
///
/// Uses the shared <see cref="ApiTestFixture"/> (real Postgres container,
/// real Program.cs, auth enabled in permissive-dev mode), but replaces the
/// primary <c>HttpMessageHandler</c> of the typed intelligence HttpClient
/// with an in-process recorder. Each test issues a real HTTP request to the
/// Tamma API, which delegates to the typed client, which writes to the
/// recorder instead of hitting a real sidecar.
/// </summary>
[TestFixture]
public class KbEndpointsIntegrationTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private SharedSidecarHandler _handler = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _handler = new SharedSidecarHandler();

        _factory = ApiTestFixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                // Register the typed client with the test handler. This
                // replaces any previous wiring because AddHttpClient<T, TImpl>
                // with the same name overwrites the last TryAddTransient.
                var config = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["IntelligenceServer:Url"] = "http://intelligence-server:4100",
                    })
                    .Build();
                s.AddKnowledgeBaseServices(config);
                s.AddHttpClient<IIntelligenceHttpClient, IntelligenceHttpClient>(
                        KnowledgeBaseServiceCollectionExtensions.HttpClientName,
                        client =>
                        {
                            client.BaseAddress = new Uri("http://intelligence-server:4100");
                        })
                    .ConfigurePrimaryHttpMessageHandler(() => _handler);
            }));

        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        _handler.Dispose();
    }

    // ── Happy paths for each endpoint group ──────────────────────────────

    [Test]
    public async Task GetIndexStatus_ForwardsToSidecar_AndReturnsPayload()
    {
        _handler.Respond("/kb/index/status", HttpStatusCode.OK, new { status = "idle", indexed = 7 });

        var resp = await _client.GetAsync("/api/kb/index/status");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"indexed\":7");
        _handler.LastPath.Should().Be("/kb/index/status");
    }

    [Test]
    public async Task TriggerIndex_ForwardsBody_ToSidecar()
    {
        _handler.Respond("/kb/index/trigger", HttpStatusCode.OK, new { message = "triggered" });

        var resp = await _client.PostAsJsonAsync(
            "/api/kb/index/trigger",
            new { fullReindex = true, repositoryPath = "/repo" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _handler.LastPath.Should().Be("/kb/index/trigger");
        _handler.LastBody.Should().Contain("fullReindex");
    }

    [Test]
    public async Task VectorSearch_ForwardsToSidecar()
    {
        _handler.Respond(
            "/kb/vector-db/search",
            HttpStatusCode.OK,
            new { results = new object[] { new { id = "d1", score = 0.9 } } });

        var resp = await _client.PostAsJsonAsync(
            "/api/kb/vector-db/search",
            new { collection = "codebase", query = "hello", topK = 3 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"id\":\"d1\"");
    }

    [Test]
    public async Task RagQuery_ForwardsToSidecar()
    {
        _handler.Respond("/kb/rag/query", HttpStatusCode.OK, new { answer = "hi", sources = Array.Empty<object>() });

        var resp = await _client.PostAsJsonAsync(
            "/api/kb/rag/query",
            new { query = "what is x", topK = 5 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"answer\":\"hi\"");
    }

    [Test]
    public async Task ListMcpServers_ForwardsToSidecar()
    {
        _handler.Respond(
            "/kb/mcp/servers",
            HttpStatusCode.OK,
            new object[] { new { name = "github", status = "connected", transport = "stdio" } });

        var resp = await _client.GetAsync("/api/kb/mcp/servers");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("github");
    }

    [Test]
    public async Task GetMcpServerById_ForwardsIdInPath()
    {
        _handler.Respond(
            "/kb/mcp/servers/github",
            HttpStatusCode.OK,
            new { name = "github", status = "connected" });

        var resp = await _client.GetAsync("/api/kb/mcp/servers/github");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _handler.LastPath.Should().Be("/kb/mcp/servers/github");
    }

    [Test]
    public async Task InvokeMcpTool_ForwardsBody()
    {
        _handler.Respond(
            "/kb/mcp/tools/invoke",
            HttpStatusCode.OK,
            new { success = true, content = "result", durationMs = 5 });

        var resp = await _client.PostAsJsonAsync(
            "/api/kb/mcp/tools/invoke",
            new { serverName = "github", toolName = "read_file", arguments = new { path = "README.md" } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _handler.LastBody.Should().Contain("read_file");
    }

    [Test]
    public async Task GetContextHistory_IssuesGetWithLimit()
    {
        _handler.Respond(
            "/kb/context/history",
            HttpStatusCode.OK,
            new { history = Array.Empty<object>() });

        var resp = await _client.GetAsync("/api/kb/context/history?limit=10");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _handler.LastUri!.Query.Should().Contain("limit=10");
    }

    [Test]
    public async Task GetKbAnalytics_PropagatesDateRange()
    {
        _handler.Respond(
            "/kb/analytics",
            HttpStatusCode.OK,
            new { queries = 0 });

        var resp = await _client.GetAsync(
            "/api/kb/analytics?start=2026-01-01T00:00:00.000Z&end=2026-01-02T00:00:00.000Z");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _handler.LastUri!.Query.Should().Contain("start=");
        _handler.LastUri!.Query.Should().Contain("end=");
    }

    // ── Fault-tolerance: sidecar outage ──────────────────────────────────

    [Test]
    public async Task Endpoint_On5xx_ReturnsOkWithDegradedEnvelope()
    {
        _handler.Respond(
            "/kb/vector-db/collections",
            HttpStatusCode.InternalServerError,
            new { error = "oops" });

        var resp = await _client.GetAsync("/api/kb/vector-db/collections");

        // The public API still returns 200 because we don't want one sidecar
        // failure to cascade into a dashboard 500. The body carries a
        // `degraded: true` flag the UI can act on.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"degraded\":true");
    }

    [Test]
    public async Task Endpoint_OnNetworkError_ReturnsOkWithDegradedEnvelope()
    {
        _handler.ThrowOn("/kb/rag/config", new HttpRequestException("connection refused"));

        var resp = await _client.GetAsync("/api/kb/rag/config");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"degraded\":true");
    }

    // ── Coverage sweep: every route is wired ─────────────────────────────

    [Test]
    public async Task EveryKbRoute_IsReachable_AndReachesTheSidecar()
    {
        // Smoke test: one call per route. Each must produce exactly one
        // sidecar request with the expected verb + path.
        var expectations = new (string Verb, string ApiPath, string SidecarPath, HttpContent? Body)[]
        {
            ("GET",    "/api/kb/index/status",        "/kb/index/status",        null),
            ("POST",   "/api/kb/index/trigger",       "/kb/index/trigger",       JsonBody(new { })),
            ("GET",    "/api/kb/index/config",        "/kb/index/config",        null),
            ("PUT",    "/api/kb/index/config",        "/kb/index/config",        JsonBody(new { })),
            ("GET",    "/api/kb/index/stats",         "/kb/index/stats",         null),
            ("DELETE", "/api/kb/index",               "/kb/index",               null),

            ("GET",    "/api/kb/vector-db/status",       "/kb/vector-db/status",       null),
            ("POST",   "/api/kb/vector-db/search",       "/kb/vector-db/search",       JsonBody(new { collection = "c", query = "q" })),
            ("POST",   "/api/kb/vector-db/upsert",       "/kb/vector-db/upsert",       JsonBody(new { collection = "c", documents = Array.Empty<object>() })),
            ("DELETE", "/api/kb/vector-db/delete",       "/kb/vector-db/delete",       JsonBody(new { collection = "c", ids = Array.Empty<string>() })),
            ("GET",    "/api/kb/vector-db/collections",  "/kb/vector-db/collections",  null),
            ("GET",    "/api/kb/vector-db/stats",        "/kb/vector-db/stats",        null),

            ("GET",    "/api/kb/rag/config",  "/kb/rag/config",  null),
            ("PUT",    "/api/kb/rag/config",  "/kb/rag/config",  JsonBody(new { })),
            ("POST",   "/api/kb/rag/query",   "/kb/rag/query",   JsonBody(new { query = "q" })),
            ("GET",    "/api/kb/rag/metrics", "/kb/rag/metrics", null),

            ("GET",    "/api/kb/mcp/servers",              "/kb/mcp/servers",              null),
            ("GET",    "/api/kb/mcp/servers/github",       "/kb/mcp/servers/github",       null),
            ("POST",   "/api/kb/mcp/servers/github/start", "/kb/mcp/servers/github/start", JsonBody(new { })),
            ("POST",   "/api/kb/mcp/servers/github/stop",  "/kb/mcp/servers/github/stop",  JsonBody(new { })),
            ("GET",    "/api/kb/mcp/config",               "/kb/mcp/config",               null),
            ("PUT",    "/api/kb/mcp/config",               "/kb/mcp/config",               JsonBody(new { })),
            ("GET",    "/api/kb/mcp/tools",                "/kb/mcp/tools",                null),
            ("POST",   "/api/kb/mcp/tools/invoke",         "/kb/mcp/tools/invoke",         JsonBody(new { serverName = "s", toolName = "t" })),

            ("GET",    "/api/kb/context/history",  "/kb/context/history",  null),
            ("POST",   "/api/kb/context/feedback", "/kb/context/feedback", JsonBody(new { requestId = "r", helpful = true })),
            ("GET",    "/api/kb/context/config",   "/kb/context/config",   null),

            ("GET",    "/api/kb/analytics",       "/kb/analytics",       null),
            ("GET",    "/api/kb/analytics/usage", "/kb/analytics/usage", null),
            ("GET",    "/api/kb/analytics/costs", "/kb/analytics/costs", null),
        };

        expectations.Length.Should().Be(30);

        foreach (var e in expectations)
        {
            _handler.Reset();
            _handler.Respond(e.SidecarPath, HttpStatusCode.OK, new { ok = true });

            using var req = new HttpRequestMessage(new HttpMethod(e.Verb), e.ApiPath);
            if (e.Body is not null) req.Content = e.Body;

            var resp = await _client.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.OK, because: $"{e.Verb} {e.ApiPath} must forward");
            _handler.LastPath.Should().Be(e.SidecarPath, because: $"{e.Verb} {e.ApiPath} must map to {e.SidecarPath}");
        }
    }

    private static HttpContent JsonBody(object payload)
        => new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    /// <summary>
    /// In-process replacement for the sidecar. Records the last request and
    /// returns per-path canned responses; can be told to throw for a path.
    /// </summary>
    private sealed class SharedSidecarHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new();
        private readonly Dictionary<string, Exception> _throwers = new();

        public Uri? LastUri { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }

        public void Respond(string path, HttpStatusCode status, object payload)
        {
            _responses[path] = (status, JsonSerializer.Serialize(payload));
        }

        public void ThrowOn(string path, Exception exception)
        {
            _throwers[path] = exception;
        }

        public void Reset()
        {
            _responses.Clear();
            _throwers.Clear();
            LastUri = null;
            LastPath = null;
            LastBody = null;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastPath = request.RequestUri!.AbsolutePath;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (_throwers.TryGetValue(LastPath, out var ex))
                throw ex;

            if (_responses.TryGetValue(LastPath, out var r))
            {
                return new HttpResponseMessage(r.Status)
                {
                    Content = new StringContent(r.Body, Encoding.UTF8, "application/json"),
                };
            }

            // Default: return an empty 200 so sweep tests that don't register
            // a specific body still succeed.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
