using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Dtos.KnowledgeBase;
using Tamma.Api.Services.KnowledgeBase;

namespace Tamma.Api.Tests.KnowledgeBase;

/// <summary>
/// Unit tests for <see cref="IntelligenceHttpClient"/>. Uses a fake
/// <see cref="HttpMessageHandler"/> so no real network or sidecar is needed —
/// each test asserts the client produces the correct HTTP verb, path, and
/// body for one of the 30 KB operations.
/// </summary>
[TestFixture]
public class IntelligenceHttpClientTests
{
    private RecordingHttpHandler _handler = null!;
    private HttpClient _httpClient = null!;
    private IntelligenceHttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new RecordingHttpHandler();
        _httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri("http://intelligence-server:4100"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        _client = new IntelligenceHttpClient(_httpClient, NullLogger<IntelligenceHttpClient>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    // ── index routes (6) ────────────────────────────────────────────────────

    [Test]
    public async Task GetIndexStatusAsync_IssuesGetRequest_AgainstExpectedPath()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { status = "idle", indexed = 0, pending = 0 });
        _ = await _client.GetIndexStatusAsync();
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/kb/index/status");
    }

    [Test]
    public async Task TriggerIndexAsync_PostsBody()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { message = "Indexing triggered" });
        var req = new TriggerIndexRequest(true, "/tmp/r", null);
        _ = await _client.TriggerIndexAsync(req);
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/kb/index/trigger");
        var body = await _handler.LastRequest.Content!.ReadAsStringAsync();
        body.Should().Contain("\"fullReindex\":true");
    }

    [Test]
    public async Task GetIndexConfigAsync_IssuesGetRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { configured = false });
        _ = await _client.GetIndexConfigAsync();
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/kb/index/config");
    }

    [Test]
    public async Task UpdateIndexConfigAsync_IssuesPutRequest_WithBody()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { });
        var req = new UpdateIndexConfigRequest(new[] { "**/*.rs" }, null, null, null, null);
        _ = await _client.UpdateIndexConfigAsync(req);
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Put);
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/kb/index/config");
        var body = await _handler.LastRequest.Content!.ReadAsStringAsync();
        body.Should().Contain("includePatterns");
    }

    [Test]
    public async Task GetIndexStatsAsync_IssuesGetRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { documents = 0, chunks = 0 });
        _ = await _client.GetIndexStatsAsync();
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/kb/index/stats");
    }

    [Test]
    public async Task ClearIndexAsync_IssuesDeleteRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { message = "Index cleared" });
        _ = await _client.ClearIndexAsync();
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/kb/index");
    }

    // ── vector-db routes (6) ────────────────────────────────────────────────

    [Test]
    public async Task GetVectorDbStatusAsync_IssuesGetRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { status = "ready" });
        _ = await _client.GetVectorDbStatusAsync();
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/kb/vector-db/status");
    }

    [Test]
    public async Task SearchVectorsAsync_PostsBody()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { results = Array.Empty<object>() });
        _ = await _client.SearchVectorsAsync(new VectorSearchRequest("c", "q", 5));
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/kb/vector-db/search");
    }

    [Test]
    public async Task UpsertVectorsAsync_PostsBody_WithDocuments()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { message = "ok", count = 1 });
        var req = new VectorUpsertRequest(
            "c",
            new[] { new VectorDocument("d1", new[] { 0.1, 0.2 }, "hi", null) });
        _ = await _client.UpsertVectorsAsync(req);
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/kb/vector-db/upsert");
    }

    [Test]
    public async Task DeleteVectorsAsync_IssuesDeleteRequest_WithBody()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { message = "ok" });
        _ = await _client.DeleteVectorsAsync(new VectorDeleteRequest("c", new[] { "d1" }));
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/kb/vector-db/delete");
    }

    [Test]
    public async Task GetVectorCollectionsAsync_IssuesGetRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, Array.Empty<object>());
        _ = await _client.GetVectorCollectionsAsync();
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/kb/vector-db/collections");
    }

    [Test]
    public async Task GetVectorStatsAsync_IssuesGetRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { totalVectors = 0 });
        _ = await _client.GetVectorStatsAsync();
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/kb/vector-db/stats");
    }

    // ── rag routes (4) ──────────────────────────────────────────────────────

    [Test]
    public async Task GetRagConfigAsync_IssuesGetRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { enabled = false });
        _ = await _client.GetRagConfigAsync();
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/kb/rag/config");
    }

    [Test]
    public async Task UpdateRagConfigAsync_IssuesPutRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { });
        _ = await _client.UpdateRagConfigAsync(new UpdateRagConfigRequest(true, null, null, null, null));
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Put);
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/kb/rag/config");
    }

    [Test]
    public async Task QueryRagAsync_PostsBody()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { answer = "", sources = Array.Empty<object>() });
        _ = await _client.QueryRagAsync(new RagQueryRequest("q", 5, null, null));
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/kb/rag/query");
    }

    [Test]
    public async Task GetRagMetricsAsync_IssuesGetRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { queries = 0 });
        _ = await _client.GetRagMetricsAsync();
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/kb/rag/metrics");
    }

    // ── mcp routes (8) ──────────────────────────────────────────────────────

    [Test]
    public async Task ListMcpServersAsync_IssuesGetRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, Array.Empty<object>());
        _ = await _client.ListMcpServersAsync();
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/kb/mcp/servers");
    }

    [Test]
    public async Task GetMcpServerAsync_EmbedsIdInPath()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { name = "github" });
        _ = await _client.GetMcpServerAsync("github");
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/kb/mcp/servers/github");
    }

    [Test]
    public async Task StartMcpServerAsync_IssuesPostRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { message = "started" });
        _ = await _client.StartMcpServerAsync("github");
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/kb/mcp/servers/github/start");
    }

    [Test]
    public async Task StopMcpServerAsync_IssuesPostRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { message = "stopped" });
        _ = await _client.StopMcpServerAsync("files");
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/kb/mcp/servers/files/stop");
    }

    [Test]
    public async Task GetMcpConfigAsync_IssuesGetRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { servers = Array.Empty<object>() });
        _ = await _client.GetMcpConfigAsync();
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/kb/mcp/config");
    }

    [Test]
    public async Task UpdateMcpConfigAsync_IssuesPutRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { });
        _ = await _client.UpdateMcpConfigAsync(new UpdateMcpConfigRequest(null));
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Put);
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/kb/mcp/config");
    }

    [Test]
    public async Task ListMcpToolsAsync_IssuesGetRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, Array.Empty<object>());
        _ = await _client.ListMcpToolsAsync();
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/kb/mcp/tools");
    }

    [Test]
    public async Task InvokeMcpToolAsync_PostsBody()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { success = true });
        _ = await _client.InvokeMcpToolAsync(new McpInvokeRequest("s", "t", null));
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/kb/mcp/tools/invoke");
    }

    // ── context routes (3) ──────────────────────────────────────────────────

    [Test]
    public async Task GetContextHistoryAsync_IssuesGetRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { history = Array.Empty<object>() });
        _ = await _client.GetContextHistoryAsync();
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/kb/context/history");
    }

    [Test]
    public async Task PostContextFeedbackAsync_IssuesPostRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { });
        _ = await _client.PostContextFeedbackAsync(new ContextFeedbackRequest("r1", true, null));
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/kb/context/feedback");
    }

    [Test]
    public async Task GetContextConfigAsync_IssuesGetRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { maxTokens = 100000 });
        _ = await _client.GetContextConfigAsync();
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/kb/context/config");
    }

    // ── analytics routes (3) ────────────────────────────────────────────────

    [Test]
    public async Task GetAnalyticsAsync_IssuesGetRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { queries = 0 });
        _ = await _client.GetAnalyticsAsync();
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/kb/analytics");
    }

    [Test]
    public async Task GetUsageAsync_IssuesGetRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { daily = Array.Empty<object>() });
        _ = await _client.GetUsageAsync();
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/kb/analytics/usage");
    }

    [Test]
    public async Task GetCostsAsync_IssuesGetRequest()
    {
        _handler.RespondWith(HttpStatusCode.OK, new { totalCost = 0.0 });
        _ = await _client.GetCostsAsync();
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/kb/analytics/costs");
    }

    // ── fault tolerance ──────────────────────────────────────────────────────

    [Test]
    public async Task AnyGetEndpoint_On5xx_ReturnsFallbackPayload_InsteadOfThrowing()
    {
        // Circuit-breaker-light: when the sidecar is degraded, GET operations
        // must return a deserialised empty envelope rather than bubbling an
        // HttpRequestException to the dashboard.
        _handler.RespondWith(HttpStatusCode.InternalServerError, new { error = "oops" });
        var status = await _client.GetIndexStatusAsync();
        status.Should().NotBeNull();
    }

    [Test]
    public async Task AnyEndpoint_OnTimeout_ReturnsFallback()
    {
        _handler.ThrowOnRequest = new TaskCanceledException("timeout");
        var status = await _client.GetIndexStatusAsync();
        status.Should().NotBeNull();
    }
}

/// <summary>
/// Recording HttpMessageHandler that captures the last request and returns a
/// canned response. Supports simulating 5xx, exceptions, and normal payloads.
/// </summary>
internal sealed class RecordingHttpHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public Exception? ThrowOnRequest { get; set; }

    private HttpStatusCode _status = HttpStatusCode.OK;
    private string _body = "{}";

    public void RespondWith(HttpStatusCode status, object payload)
    {
        _status = status;
        _body = JsonSerializer.Serialize(payload);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Snapshot the content because the original stream is unreliable after
        // SendAsync returns. `request.Content` itself remains addressable.
        if (request.Content is not null)
        {
            _ = await request.Content.ReadAsStringAsync(cancellationToken);
        }
        LastRequest = request;

        if (ThrowOnRequest is not null)
            throw ThrowOnRequest;

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        };
    }
}
