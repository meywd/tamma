using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Models;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.SaaS;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Tests.Providers;

/// <summary>
/// Provider-abstraction Phase 1 — GOLDEN request-shaping tests
/// (.dev/findings/provider-abstraction-and-openai-compatible-candidates.md).
///
/// <para>These tests pin the EXACT bytes (request body, URL, auth + version
/// headers) each of the three LLM egress paths puts on the wire:</para>
/// <list type="number">
///   <item><see cref="InlineToolLoopRunner"/> — the agentic tool loop
///     (multi-turn Anthropic + OpenAI-compatible dialects, incl. tools);</item>
///   <item><see cref="Tamma.Api.Services.Providers.HttpProviderClient"/> — the
///     provider-session dispatch layer;</item>
///   <item><see cref="LlmProxyService"/> — the SaaS Anthropic pass-through proxy.</item>
/// </list>
///
/// <para>They were written against the PRE-descriptor code and verified green
/// BEFORE the descriptor refactor, so a green run after the refactor is the
/// behaviour-preservation proof the finding requires: collapsing the three
/// duplicated dialect branches behind <c>ProviderCatalog</c> /
/// <c>ProviderRequestShaper</c> changed no byte of what currently-working
/// providers send.</para>
/// </summary>
[TestFixture]
public class ProviderGoldenRequestTests
{
    // ── shared harness ──────────────────────────────────────────────────────

    /// <summary>Captures every outbound request (URL, headers, body) and replays
    /// a scripted queue of responses.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<(Uri? Uri, HttpRequestMessage Request, string Body)> Captured { get; } = new();

        public void EnqueueJson(string json) =>
            _responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Captured.Add((request.RequestUri, request, body));
            return _responses.Dequeue();
        }
    }

    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        private readonly CapturingHandler _handler;
        public List<string> RequestedNames { get; } = new();
        public Uri? BaseAddress { get; init; }

        public RecordingHttpClientFactory(CapturingHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name)
        {
            RequestedNames.Add(name);
            var client = new HttpClient(_handler, disposeHandler: false);
            if (BaseAddress is not null) client.BaseAddress = BaseAddress;
            return client;
        }
    }

    private static IReadOnlyList<ResolvedTool> WeatherTool() => new[]
    {
        new ResolvedTool
        {
            Name = "get_weather",
            Description = "Get weather",
            InputSchema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["city"] = new Dictionary<string, object> { ["type"] = "string" },
                },
                ["required"] = new[] { "city" },
            },
        },
    };

    private static InlineToolLoopRunner Runner(IHttpClientFactory factory) => new(
        logger: null,
        httpClientFactory: factory,
        configuration: null,
        sanitizer: null,
        autonomyGate: new Tamma.Api.Services.Agents.CatalogDefaultToolLoopAutonomyGate());

    // Tool result the loop feeds back when no IToolExecutorRegistry is wired.
    private const string NoRegistryToolResult =
        "Tool execution not available (registry not configured)";

    // ── 1. InlineToolLoopRunner — Anthropic dialect ─────────────────────────

    [Test]
    public async Task Runner_Anthropic_MultiTurnWithTools_GoldenBytes()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson("""
            {"content":[{"type":"text","text":"Let me check."},{"type":"tool_use","id":"toolu_1","name":"get_weather","input":{"city":"Berlin"}}],"usage":{"input_tokens":10,"output_tokens":5},"stop_reason":"tool_use","model":"claude-sonnet-4-20250514"}
            """);
        handler.EnqueueJson("""
            {"content":[{"type":"text","text":"Done."}],"usage":{"input_tokens":20,"output_tokens":3},"stop_reason":"end_turn","model":"claude-sonnet-4-20250514"}
            """);
        var factory = new RecordingHttpClientFactory(handler);

        var result = await Runner(factory).RunAsync(
            "anthropic",
            new LlmProviderConfig { Name = "anthropic", ApiKey = "test-key" },
            "claude-sonnet-4-20250514",
            "You are a test.",
            "Do the thing.",
            maxTokens: 512,
            temperature: 0.2,
            tools: WeatherTool(),
            enableToolLoop: true,
            loopConfig: new ToolLoopConfig { MaxSteps = 3 },
            correlationId: "golden-corr",
            repair: null,
            ct: CancellationToken.None);

        result.Response.Success.Should().BeTrue();
        handler.Captured.Should().HaveCount(2);

        // Phantom-client fix (documented divergence): the runner used to ask the
        // factory for "llm-{provider}" — a name no registration ever matched, so
        // it always received an unconfigured default client. It now asks for ONE
        // intentional plain-client name and configures everything per call.
        factory.RequestedNames.Should().ContainSingle()
            .Which.Should().Be(InlineToolLoopRunner.RunnerHttpClientName);

        // URL — empty BaseUrl falls back to the public Anthropic endpoint.
        handler.Captured[0].Uri.Should().Be(new Uri("https://api.anthropic.com/v1/messages"));
        handler.Captured[1].Uri.Should().Be(new Uri("https://api.anthropic.com/v1/messages"));

        // Auth + version headers. 2023-06-01 is per-descriptor DATA, pinned here.
        foreach (var (_, request, _) in handler.Captured)
        {
            request.Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be("test-key");
            request.Headers.GetValues("anthropic-version").Should().ContainSingle().Which.Should().Be("2023-06-01");
        }

        handler.Captured[0].Body.Should().Be(
            """{"model":"claude-sonnet-4-20250514","max_tokens":512,"temperature":0.2,"system":"You are a test.","messages":[{"role":"user","content":"Do the thing."}],"tools":[{"name":"get_weather","description":"Get weather","input_schema":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}]}""");

        handler.Captured[1].Body.Should().Be(
            """{"model":"claude-sonnet-4-20250514","max_tokens":512,"temperature":0.2,"system":"You are a test.","messages":[{"role":"user","content":"Do the thing."},{"role":"assistant","content":[{"type":"text","text":"Let me check."},{"type":"tool_use","id":"toolu_1","name":"get_weather","input":{"city":"Berlin"}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"Tool execution not available (registry not configured)"}]}],"tools":[{"name":"get_weather","description":"Get weather","input_schema":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}]}""");
    }

    // ── 2. InlineToolLoopRunner — OpenAI-compatible dialect ─────────────────

    [Test]
    public async Task Runner_OpenAi_MultiTurnWithTools_GoldenBytes()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson("""
            {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Berlin\"}"}}]}}],"usage":{"prompt_tokens":10,"completion_tokens":5},"model":"gpt-4o"}
            """);
        handler.EnqueueJson("""
            {"choices":[{"finish_reason":"stop","message":{"content":"Done."}}],"usage":{"prompt_tokens":20,"completion_tokens":3},"model":"gpt-4o"}
            """);
        var factory = new RecordingHttpClientFactory(handler);

        var result = await Runner(factory).RunAsync(
            "openai",
            new LlmProviderConfig { Name = "openai", ApiKey = "test-key" },
            "gpt-4o",
            "You are a test.",
            "Do the thing.",
            maxTokens: 512,
            temperature: 0.2,
            tools: WeatherTool(),
            enableToolLoop: true,
            loopConfig: new ToolLoopConfig { MaxSteps = 3 },
            correlationId: "golden-corr",
            repair: null,
            ct: CancellationToken.None);

        result.Response.Success.Should().BeTrue();
        handler.Captured.Should().HaveCount(2);

        handler.Captured[0].Uri.Should().Be(new Uri("https://api.openai.com/v1/chat/completions"));
        handler.Captured[1].Uri.Should().Be(new Uri("https://api.openai.com/v1/chat/completions"));

        foreach (var (_, request, _) in handler.Captured)
        {
            request.Headers.Authorization!.ToString().Should().Be("Bearer test-key");
        }

        handler.Captured[0].Body.Should().Be(
            """{"model":"gpt-4o","max_tokens":512,"temperature":0.2,"messages":[{"role":"system","content":"You are a test."},{"role":"user","content":"Do the thing."}],"tools":[{"type":"function","function":{"name":"get_weather","description":"Get weather","parameters":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}}]}""");

        // The assistant echo of the tool call carries the raw arguments JSON as a
        // STRING value; System.Text.Json's default encoder escapes the embedded
        // quotes as the six-character sequence backslash-u-0-0-2-2. __Q__ marks
        // that escape sequence (kept out of the raw string literal so the
        // expected body stays readable).
        var escapedQuote = new string(new[] { '\\', 'u', '0', '0', '2', '2' });
        handler.Captured[1].Body.Should().Be(
            """{"model":"gpt-4o","max_tokens":512,"temperature":0.2,"messages":[{"role":"system","content":"You are a test."},{"role":"user","content":"Do the thing."},{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{__Q__city__Q__:__Q__Berlin__Q__}"}}]},{"role":"tool","tool_call_id":"call_1","content":"Tool execution not available (registry not configured)"}],"tools":[{"type":"function","function":{"name":"get_weather","description":"Get weather","parameters":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}}]}"""
                .Replace("__Q__", escapedQuote));
    }

    // ── 3. HttpProviderClient — Anthropic dialect ───────────────────────────

    [Test]
    public async Task HttpProviderClient_Anthropic_GoldenBytes()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson("""
            {"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1,"output_tokens":2}}
            """);
        var factory = new RecordingHttpClientFactory(handler)
        {
            BaseAddress = new Uri("https://api.anthropic.com"),
        };
        var client = new HttpProviderClient(
            factory, Mock.Of<IProviderPricingService>(), NullLogger<HttpProviderClient>.Instance);

        var result = await client.InvokeAsync(
            "anthropic", "claude-x", new ExecuteRequest("handle", "hello", null, null));

        result.Content.Should().Be("ok");
        factory.RequestedNames.Should().ContainSingle().Which.Should().Be("anthropic");
        handler.Captured.Should().ContainSingle();
        handler.Captured[0].Uri.Should().Be(new Uri("https://api.anthropic.com/v1/messages"));
        handler.Captured[0].Body.Should().Be(
            """{"model":"claude-x","max_tokens":1024,"temperature":null,"messages":[{"role":"user","content":"hello"}]}""");
    }

    // ── 4. HttpProviderClient — OpenAI-compatible dialect ───────────────────

    [Test]
    public async Task HttpProviderClient_OpenAi_GoldenBytes()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson("""
            {"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
            """);
        var factory = new RecordingHttpClientFactory(handler)
        {
            BaseAddress = new Uri("https://api.openai.com"),
        };
        var client = new HttpProviderClient(
            factory, Mock.Of<IProviderPricingService>(), NullLogger<HttpProviderClient>.Instance);

        var result = await client.InvokeAsync(
            "openai", "gpt-x", new ExecuteRequest("handle", "hello", null, null));

        result.Content.Should().Be("ok");
        factory.RequestedNames.Should().ContainSingle().Which.Should().Be("openai");
        handler.Captured.Should().ContainSingle();
        handler.Captured[0].Uri.Should().Be(new Uri("https://api.openai.com/v1/chat/completions"));
        handler.Captured[0].Body.Should().Be(
            """{"model":"gpt-x","max_tokens":null,"temperature":null,"messages":[{"role":"user","content":"hello"}]}""");
    }

    // ── 5/6. LlmProxyService — Anthropic pass-through proxy ─────────────────

    private static LlmProxyService Proxy(IHttpClientFactory factory)
    {
        var tagger = new Mock<IBillingModeTagger>();
        tagger
            .Setup(t => t.ResolveTagAsync(
                It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("platform");
        var diagnostics = new Mock<IDiagnosticsService>();
        diagnostics
            .Setup(d => d.RecordEventAsync(It.IsAny<Tamma.Data.Entities.ProviderDiagnostic>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        return new LlmProxyService(
            factory,
            diagnostics.Object,
            tagger.Object,
            Mock.Of<IEventRepository>(),
            NullLogger<LlmProxyService>.Instance);
    }

    [Test]
    public async Task LlmProxy_Anthropic_NoTemperature_GoldenBytes()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson("""
            {"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1,"output_tokens":2},"model":"claude-sonnet-4.5"}
            """);
        var factory = new RecordingHttpClientFactory(handler)
        {
            BaseAddress = new Uri("https://api.anthropic.com"),
        };

        var response = await Proxy(factory).ChatAsync(
            new ChatRequest(
                "claude-sonnet-4.5",
                new[] { new ChatMessage("system", "S"), new ChatMessage("user", "U"), new ChatMessage("assistant", "A") },
                MaxTokens: null,
                Temperature: null),
            tenantId: null);

        response.Success.Should().BeTrue();
        factory.RequestedNames.Should().ContainSingle().Which.Should().Be("anthropic");
        handler.Captured.Should().ContainSingle();
        handler.Captured[0].Uri.Should().Be(new Uri("https://api.anthropic.com/v1/messages"));
        handler.Captured[0].Body.Should().Be(
            """{"model":"claude-sonnet-4.5","max_tokens":1024,"messages":[{"role":"user","content":"U"},{"role":"assistant","content":"A"}],"system":"S"}""");
    }

    [Test]
    public async Task LlmProxy_Anthropic_WithTemperature_GoldenBytes()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson("""
            {"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1,"output_tokens":2},"model":"claude-sonnet-4.5"}
            """);
        var factory = new RecordingHttpClientFactory(handler)
        {
            BaseAddress = new Uri("https://api.anthropic.com"),
        };

        var response = await Proxy(factory).ChatAsync(
            new ChatRequest(
                "claude-sonnet-4.5",
                new[] { new ChatMessage("user", "U") },
                MaxTokens: 64,
                Temperature: 0.5),
            tenantId: null);

        response.Success.Should().BeTrue();
        handler.Captured.Should().ContainSingle();
        handler.Captured[0].Body.Should().Be(
            """{"model":"claude-sonnet-4.5","max_tokens":64,"messages":[{"role":"user","content":"U"}],"temperature":0.5}""");
    }
}
