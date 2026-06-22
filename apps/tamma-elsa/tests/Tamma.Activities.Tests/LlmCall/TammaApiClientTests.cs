using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Tests.LlmCall;

/// <summary>
/// Story 9-11: Tests for <see cref="TammaApiClient"/>.
///
/// Every test uses a <see cref="StubHttpMessageHandler"/> to intercept the
/// request and return a canned response. No real network traffic.
/// </summary>
[TestFixture]
public class TammaApiClientTests
{
    private static TammaApiClient BuildClient(
        StubHttpMessageHandler handler,
        IDictionary<string, string?>? config = null)
    {
        var http = new HttpClient(handler) { BaseAddress = null };
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(config ?? new Dictionary<string, string?>
            {
                ["Tamma:ApiUrl"] = "http://tamma.test",
            })
            .Build();
        return new TammaApiClient(http, NullLogger<TammaApiClient>.Instance, cfg);
    }

    [Test]
    public async Task ResolveAgentAsync_ReturnsParsedResult_OnSuccessfulResponse()
    {
        var payload = new AgentResolveResult(
            Role: "developer",
            Handle: "tamma-developer",
            Provider: "anthropic",
            Model: "claude-sonnet-4",
            Temperature: 0.5,
            MaxTokens: 4096,
            TokenBudget: 16384,
            Tools: new[] { "Read", "Write" },
            SystemPrompt: "You are a developer",
            Source: "platform-default",
            Phase: null,
            MaxBudgetUsd: 1.5m,
            PermissionMode: "default",
            AllowedTools: null);
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, JsonSerializer.Serialize(payload));
        var client = BuildClient(handler);

        var result = await client.ResolveAgentAsync("developer");

        result.Should().NotBeNull();
        result!.Role.Should().Be("developer");
        result.Provider.Should().Be("anthropic");
        result.Model.Should().Be("claude-sonnet-4");
        handler.LastRequest!.RequestUri!.AbsolutePath
            .Should().Be("/api/v1/agents/developer/resolve");
    }

    [Test]
    public async Task ResolveAgentAsync_ReturnsNull_On5xx()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "{}");
        var client = BuildClient(handler);

        var result = await client.ResolveAgentAsync("developer");

        result.Should().BeNull();
    }

    [Test]
    public async Task ResolveAgentAsync_ReturnsNull_OnNetworkException()
    {
        var handler = new StubHttpMessageHandler(new HttpRequestException("connection refused"));
        var client = BuildClient(handler);

        var result = await client.ResolveAgentAsync("developer");

        result.Should().BeNull();
    }

    [Test]
    public async Task GetProviderHealthAsync_ReturnsStatus_AndUsesCorrectUrl()
    {
        var status = new ProviderHealthStatus(
            Key: "anthropic",
            Healthy: true,
            Failures: 0,
            CircuitOpen: false,
            CircuitOpenUntil: null,
            HalfOpen: false);
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, JsonSerializer.Serialize(status));
        var client = BuildClient(handler);

        var result = await client.GetProviderHealthAsync("anthropic");

        result.Should().NotBeNull();
        result!.Healthy.Should().BeTrue();
        handler.LastRequest!.RequestUri!.AbsolutePath
            .Should().Be("/api/providers/health/providers/anthropic");
    }

    [Test]
    public async Task GetBudgetAsync_UsesEncodedAccountId()
    {
        var body = new BudgetStatus(Spent: 10m, Limit: 100m, Remaining: 90m, PercentUsed: 10m);
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, JsonSerializer.Serialize(body));
        var client = BuildClient(handler);

        await client.GetBudgetAsync("tenant/with/slash");

        handler.LastRequest!.RequestUri!.AbsolutePath
            .Should().Be("/api/providers/diagnostics/budget/tenant%2Fwith%2Fslash");
    }

    [Test]
    public async Task RecordDiagnosticsAsync_PostsBody_AndReturnsTrueOnOk()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.Accepted, "{}");
        var client = BuildClient(handler);

        var ok = await client.RecordDiagnosticsAsync(new DiagnosticsIngestRequest(
            Provider: "anthropic",
            Model: "claude-sonnet-4",
            Role: "developer",
            Action: "implement",
            Success: true,
            PromptTokens: 100,
            CompletionTokens: 50,
            TotalTokens: 150,
            CostUsd: 0.001m,
            DurationMs: 1200,
            ErrorMessage: null,
            AccountId: "tenant-a",
            CorrelationId: "corr-1"));

        ok.Should().BeTrue();
        handler.LastRequest!.RequestUri!.AbsolutePath
            .Should().Be("/api/providers/diagnostics");
        handler.LastRequest.Method.Should().Be(HttpMethod.Post);
    }

    [Test]
    public async Task RecordDiagnosticsAsync_ReturnsFalse_OnNetworkError()
    {
        var handler = new StubHttpMessageHandler(new HttpRequestException("boom"));
        var client = BuildClient(handler);

        var ok = await client.RecordDiagnosticsAsync(new DiagnosticsIngestRequest(
            Provider: "x", Model: null, Role: null, Action: null,
            Success: false, PromptTokens: 0, CompletionTokens: 0, TotalTokens: 0,
            CostUsd: 0, DurationMs: 0, ErrorMessage: null, AccountId: null,
            CorrelationId: null));

        ok.Should().BeFalse();
    }

    [Test]
    public async Task AnyCall_AddsAuthorizationHeader_WhenTokenConfigured()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler, new Dictionary<string, string?>
        {
            ["Tamma:ApiUrl"] = "http://tamma.test",
            ["Tamma:ApiToken"] = "tamma_sk_test",
        });

        await client.GetProviderHealthAsync("anthropic");

        handler.LastRequest!.Headers.Authorization.Should().NotBeNull();
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("tamma_sk_test");
    }

    [Test]
    public async Task AnyCall_AddsTenantHeader_WhenTenantIdProvided()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.GetProviderHealthAsync("anthropic", tenantId: "tenant-a");

        handler.LastRequest!.Headers.TryGetValues("X-Tenant-Id", out var values).Should().BeTrue();
        values!.Single().Should().Be("tenant-a");
    }

    [Test]
    public async Task AppendEventsAsync_PostsBatch_ToEngineEvents_AndReturnsTrueOnOk()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.Created, "{\"ok\":true,\"persisted\":2}");
        var client = BuildClient(handler);

        var id1 = Guid.NewGuid();
        var events = new List<EngineEventRecord>
        {
            new(id1, "CODE.GENERATED.SUCCESS", "success", null, DateTime.UtcNow, 12.5,
                "act-1", "GenerateCode", "wf-1", 42, null, null),
            new(Guid.NewGuid(), "CODE.GENERATED.FAILED", "error", "boom", DateTime.UtcNow, 3.0,
                "act-2", "GenerateCode", "wf-1", 42, null, null),
        };

        var ok = await client.AppendEventsAsync(events, tenantId: Guid.Parse("11111111-1111-1111-1111-111111111111"));

        ok.Should().BeTrue();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/api/engine/events");
        handler.LastRequest.Headers.TryGetValues("X-Tenant-Id", out var tenant).Should().BeTrue();
        tenant!.Single().Should().Be("11111111-1111-1111-1111-111111111111");

        // The wire body carries the camelCase batch shape. The handler reads
        // the body before the (using-scoped) request Content is disposed.
        handler.LastBody.Should().NotBeNull();
        var body = JsonDocument.Parse(handler.LastBody!).RootElement;
        body.GetProperty("events").GetArrayLength().Should().Be(2);
        body.GetProperty("events")[0].GetProperty("eventType").GetString().Should().Be("CODE.GENERATED.SUCCESS");
        body.GetProperty("events")[0].GetProperty("workflowInstanceId").GetString().Should().Be("wf-1");
        // Stable per-event id is on the wire (C2 — drives idempotent append).
        body.GetProperty("events")[0].GetProperty("id").GetGuid().Should().Be(id1);
    }

    [Test]
    public async Task AppendEventsAsync_ReturnsFalse_OnPartialFailure502()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.BadGateway, "{\"error\":\"partial_append_failure\",\"persisted\":1,\"failed\":1}");
        var client = BuildClient(handler);

        var ok = await client.AppendEventsAsync(new List<EngineEventRecord>
        {
            new(Guid.NewGuid(), "A", "success", null, DateTime.UtcNow, null, null, null, "wf", null, null, null),
        });

        ok.Should().BeFalse("a non-2xx must signal the drain to NOT advance its cursor");
    }

    [Test]
    public async Task AppendEventsAsync_ReturnsFalse_OnNetworkError()
    {
        var handler = new StubHttpMessageHandler(new HttpRequestException("down"));
        var client = BuildClient(handler);

        var ok = await client.AppendEventsAsync(new List<EngineEventRecord>
        {
            new(Guid.NewGuid(), "A", "success", null, DateTime.UtcNow, null, null, null, "wf", null, null, null),
        });

        ok.Should().BeFalse();
    }

    [Test]
    public async Task AppendEventsAsync_EmptyBatch_IsSuccessfulNoOp_AndSendsNoRequest()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.Created, "{}");
        var client = BuildClient(handler);

        var ok = await client.AppendEventsAsync(new List<EngineEventRecord>());

        ok.Should().BeTrue();
        handler.LastRequest.Should().BeNull("an empty batch must not hit the network");
    }

    [Test]
    public async Task DisposeProviderAsync_SendsDelete_AndReturnsFalseOnFailure()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "{}");
        var client = BuildClient(handler);

        var ok = await client.DisposeProviderAsync("handle-1");

        ok.Should().BeFalse();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        handler.LastRequest.RequestUri!.AbsolutePath
            .Should().Be("/api/providers/providers/handle-1");
    }

    [Test]
    public void Constructor_PrefersTammaApiUrlConfig_OverEnvVar()
    {
        // Arrange config with explicit URL
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler, new Dictionary<string, string?>
        {
            ["Tamma:ApiUrl"] = "http://explicit.test",
        });

        client.BaseUrl.Should().Be("http://explicit.test");
    }

    [Test]
    public async Task Constructor_StripsTrailingSlash_FromBaseUrl()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler, new Dictionary<string, string?>
        {
            ["Tamma:ApiUrl"] = "http://tamma.test/",
        });

        await client.GetProviderHealthAsync("x");

        handler.LastRequest!.RequestUri!.AbsoluteUri
            .Should().Be("http://tamma.test/api/providers/health/providers/x");
    }

    // -------------------------------------------------------------------
    // Test double
    // -------------------------------------------------------------------

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage? _response;
        private readonly Exception? _exception;

        public HttpRequestMessage? LastRequest { get; private set; }

        /// <summary>Request body captured BEFORE the using-scoped request
        /// (and its Content) is disposed by the client method.</summary>
        public string? LastBody { get; private set; }

        public StubHttpMessageHandler(HttpStatusCode status, string json)
        {
            _response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        public StubHttpMessageHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            if (_exception is not null) throw _exception;
            return _response!;
        }
    }
}
