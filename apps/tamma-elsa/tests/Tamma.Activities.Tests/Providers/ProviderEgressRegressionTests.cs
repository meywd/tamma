using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Models;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.SaaS;
using Tamma.Core;

namespace Tamma.Activities.Tests.Providers;

/// <summary>
/// Regression tests for the verified review findings on the provider-descriptor
/// refactor (commit 716b20f). They extend the golden suite
/// (<see cref="ProviderGoldenRequestTests"/>, which stays UNCHANGED) with the
/// exact gaps the review identified:
/// <list type="bullet">
///   <item>F1/F2/F6 — full request URIs composed through the REAL named-client
///     registrations (<c>AddTammaProviderHttpClients</c>) + the
///     <see cref="HttpProviderClient"/> dispatch path, pinning base-path
///     preservation (groq, openrouter, azure-style bases) and the corrected
///     gemini wire facts;</item>
///   <item>F3 — a config-supplied BaseUrl restores pre-refactor proxy
///     semantics ({base}/v1/chat/completions + Authorization: Bearer) for
///     gemini and z-ai on the runner path;</item>
///   <item>F4 — unconfigured azure-openai on the runner path throws a typed
///     error instead of posting the Azure key to api.openai.com;</item>
///   <item>F5 — catalogue aliases ("kimi", "z.ai") pass the allowlist gate and
///     resolve to their canonical descriptors.</item>
/// </list>
/// </summary>
[TestFixture]
public class ProviderEgressRegressionTests
{
    // ── shared harness (mirrors the golden suite's capturing plumbing) ──────

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

        public RecordingHttpClientFactory(CapturingHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false);
    }

    private const string OpenAiStopResponse = """
        {"choices":[{"finish_reason":"stop","message":{"content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3},"model":"m"}
        """;

    /// <summary>Build an <see cref="IHttpClientFactory"/> through the REAL
    /// provider registrations (the same extension Program.cs calls), with the
    /// primary handler swapped for the capturing handler.</summary>
    private static IHttpClientFactory RealRegistrationFactory(
        CapturingHandler handler, IReadOnlyDictionary<string, string?>? config = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config ?? new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddTammaProviderHttpClients(configuration);
        services.ConfigureHttpClientDefaults(b =>
            b.ConfigurePrimaryHttpMessageHandler(() => handler));

        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    private static HttpProviderClient DispatchClient(IHttpClientFactory factory) => new(
        factory, Mock.Of<IProviderPricingService>(), NullLogger<HttpProviderClient>.Instance);

    private static InlineToolLoopRunner Runner(
        IHttpClientFactory factory, IConfiguration? configuration = null) => new(
        logger: null,
        httpClientFactory: factory,
        configuration: configuration,
        sanitizer: null,
        autonomyGate: new Tamma.Api.Services.Agents.CatalogDefaultToolLoopAutonomyGate());

    private static Task<InlineToolLoopResult> RunOnce(
        InlineToolLoopRunner runner, string provider, LlmProviderConfig config) =>
        runner.RunAsync(
            provider,
            config,
            model: "test-model",
            systemPrompt: "You are a test.",
            userPrompt: "Do the thing.",
            maxTokens: 128,
            temperature: 0.1,
            tools: null,
            enableToolLoop: true,
            loopConfig: new ToolLoopConfig { MaxSteps = 2 },
            correlationId: "egress-regr",
            repair: null,
            ct: CancellationToken.None);

    // ── F1/F2/F6 — full URIs through the REAL registration + dispatch path ──

    [TestCase("groq", "https://api.groq.com/openai/v1/chat/completions",
        Description = "F1 — groq's /openai base path is preserved (the old relative post produced https://api.groq.com/v1/... — a 404)")]
    [TestCase("openrouter", "https://openrouter.ai/api/v1/chat/completions",
        Description = "F6 — the openrouter named client's base matches the descriptor (https://openrouter.ai/api), landing on the documented endpoint")]
    [TestCase("gemini", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
        Description = "F2 — Google's OpenAI-compatible surface lives under /v1beta/openai")]
    [TestCase("openai", "https://api.openai.com/v1/chat/completions",
        Description = "F1 — a host without a base path stays byte-identical")]
    public async Task HttpProviderClient_RealRegistration_ComposesFullRequestUri(
        string provider, string expectedUri)
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson(OpenAiStopResponse);
        var factory = RealRegistrationFactory(handler);

        await DispatchClient(factory).InvokeAsync(
            provider, "test-model", new ExecuteRequest("handle", "hello", null, null));

        handler.Captured.Should().ContainSingle();
        handler.Captured[0].Uri.Should().Be(new Uri(expectedUri));
    }

    [Test]
    public async Task HttpProviderClient_Gemini_RealRegistration_SendsBearerAuth()
    {
        // F2 — the gemini named client authenticates with Authorization: Bearer
        // (the former X-Goog-Api-Key header belongs to the native Gemini
        // surface, not the OpenAI-compatible one).
        var handler = new CapturingHandler();
        handler.EnqueueJson(OpenAiStopResponse);
        var factory = RealRegistrationFactory(handler, new Dictionary<string, string?>
        {
            ["Gemini:ApiKey"] = "gemini-test-key",
        });

        await DispatchClient(factory).InvokeAsync(
            "gemini", "test-model", new ExecuteRequest("handle", "hello", null, null));

        handler.Captured.Should().ContainSingle();
        var request = handler.Captured[0].Request;
        request.Headers.Authorization!.ToString().Should().Be("Bearer gemini-test-key");
        request.Headers.Contains("X-Goog-Api-Key").Should().BeFalse();
    }

    [Test]
    public async Task HttpProviderClient_AzureStyleConfiguredBaseWithPath_IsPreserved()
    {
        // F1 — an azure-openai-style per-resource base URL carrying path
        // segments keeps them; F3 — the configured (override) base gets the
        // dialect-default path appended.
        var handler = new CapturingHandler();
        handler.EnqueueJson(OpenAiStopResponse);
        var factory = RealRegistrationFactory(handler, new Dictionary<string, string?>
        {
            ["AzureOpenAI:BaseUrl"] = "https://myresource.openai.azure.com/openai",
        });

        await DispatchClient(factory).InvokeAsync(
            "azure-openai", "test-model", new ExecuteRequest("handle", "hello", null, null));

        handler.Captured.Should().ContainSingle();
        handler.Captured[0].Uri.Should().Be(
            new Uri("https://myresource.openai.azure.com/openai/v1/chat/completions"));
    }

    [Test]
    public void CombineUrl_PreservesBasePathSegments_AndStaysByteIdenticalWithoutThem()
    {
        // F1 helper edges, independent of any client plumbing.
        ProviderCatalog.CombineUrl("https://api.groq.com/openai", "/v1/chat/completions")
            .Should().Be("https://api.groq.com/openai/v1/chat/completions");
        ProviderCatalog.CombineUrl("https://api.groq.com/openai/", "/v1/chat/completions")
            .Should().Be("https://api.groq.com/openai/v1/chat/completions");
        ProviderCatalog.CombineUrl("https://api.openai.com", "/v1/chat/completions")
            .Should().Be("https://api.openai.com/v1/chat/completions");
        // HttpClient.BaseAddress.ToString() appends "/" to a path-less URI —
        // the join must not double it.
        ProviderCatalog.CombineUrl("https://api.openai.com/", "/v1/chat/completions")
            .Should().Be("https://api.openai.com/v1/chat/completions");
    }

    // ── F3 — config-supplied BaseUrl restores pre-refactor proxy semantics ──

    [TestCase("gemini", "https://llm-proxy.internal/gemini")]
    [TestCase("z-ai", "https://llm-proxy.internal/zai")]
    public async Task Runner_ConfigSuppliedBaseUrl_UsesDialectDefaultPathAndBearer(
        string provider, string configuredBase)
    {
        // Pre-refactor, a user-configured LlmProviders:{key}:BaseUrl got
        // {base}/v1/chat/completions with Authorization: Bearer. The descriptor's
        // provider-specific ChatEndpointPath (/v1beta/openai/..., /api/paas/v4/...)
        // and auth scheme apply only at the descriptor's own DefaultBaseUrl.
        var handler = new CapturingHandler();
        handler.EnqueueJson(OpenAiStopResponse);
        var factory = new RecordingHttpClientFactory(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"LlmProviders:{provider}:BaseUrl"] = configuredBase,
            })
            .Build();
        var runner = Runner(factory, configuration);

        // The REAL config-resolution path (LoadProviderConfig reads the
        // LlmProviders section), then the runner call itself.
        var config = runner.LoadProviderConfig(provider);
        config.BaseUrl.Should().Be(configuredBase);
        config.ApiKey = "proxy-key";

        var result = await RunOnce(runner, provider, config);

        result.Response.Success.Should().BeTrue();
        handler.Captured.Should().ContainSingle();
        handler.Captured[0].Uri.Should().Be(new Uri($"{configuredBase}/v1/chat/completions"));
        handler.Captured[0].Request.Headers.Authorization!.ToString().Should().Be("Bearer proxy-key");
        handler.Captured[0].Request.Headers.Contains("X-Goog-Api-Key").Should().BeFalse();
    }

    [Test]
    public async Task Runner_Gemini_DefaultBase_UsesDescriptorPathAndBearer()
    {
        // Complement of the override rule: at the descriptor's own default base
        // the descriptor's ChatEndpointPath applies (F2 wire facts, runner path).
        var handler = new CapturingHandler();
        handler.EnqueueJson(OpenAiStopResponse);
        var factory = new RecordingHttpClientFactory(handler);
        var runner = Runner(factory);

        var config = runner.LoadProviderConfig("gemini");
        config.ApiKey = "gk";

        var result = await RunOnce(runner, "gemini", config);

        result.Response.Success.Should().BeTrue();
        handler.Captured.Should().ContainSingle();
        handler.Captured[0].Uri.Should().Be(new Uri(
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions"));
        handler.Captured[0].Request.Headers.Authorization!.ToString().Should().Be("Bearer gk");
    }

    // ── F4 — unconfigured azure-openai fails loudly on the runner path ──────

    [Test]
    public async Task Runner_AzureOpenAi_WithoutBaseUrl_ThrowsInsteadOfPosting()
    {
        // The legacy fallback silently sent the Azure key to api.openai.com as
        // a Bearer token. A descriptor with no default base URL and no config
        // BaseUrl must fail loudly, naming the missing config key.
        var handler = new CapturingHandler();
        var factory = new RecordingHttpClientFactory(handler);
        var runner = Runner(factory);

        var config = runner.LoadProviderConfig("azure-openai");
        config.BaseUrl.Should().BeEmpty("azure-openai has no default base URL");
        config.ApiKey = "azure-key";

        var act = () => RunOnce(runner, "azure-openai", config);

        var thrown = await act.Should().ThrowAsync<TammaError>();
        thrown.Which.Code.Should().Be("PROVIDER.BASE_URL.MISSING");
        thrown.Which.Message.Should().Contain("LlmProviders:azure-openai:BaseUrl");
        handler.Captured.Should().BeEmpty("nothing may be posted anywhere");
    }

    // ── F5 — aliases pass the allowlist gate and resolve canonically ────────

    [Test]
    public void LoadProviderConfig_Kimi_PassesAllowlist_AndResolvesToMoonshot()
    {
        var runner = Runner(new RecordingHttpClientFactory(new CapturingHandler()));

        var config = runner.LoadProviderConfig("kimi");

        config.Enabled.Should().BeTrue("'kimi' is a catalogue alias of the allow-listed 'moonshot'");
        config.Name.Should().Be("moonshot");
        config.BaseUrl.Should().Be("https://api.moonshot.ai");
        config.DefaultModel.Should().Be("kimi-k3");

        // The public surface agrees.
        runner.GetDefaultModel("kimi").Should().Be("kimi-k3");
    }

    [Test]
    public void LoadProviderConfig_ZDotAi_PassesAllowlist_AndResolvesToZAi()
    {
        var runner = Runner(new RecordingHttpClientFactory(new CapturingHandler()));

        foreach (var spelling in new[] { "z.ai", "zai" })
        {
            var config = runner.LoadProviderConfig(spelling);
            config.Enabled.Should().BeTrue(
                $"'{spelling}' is a catalogue alias of the allow-listed 'z-ai'");
            config.Name.Should().Be("z-ai");
            config.BaseUrl.Should().Be("https://api.z.ai");
            config.DefaultModel.Should().Be("glm-5.2");
        }
    }

    [Test]
    public void LoadProviderConfig_UnknownProvider_IsStillRejected()
    {
        var runner = Runner(new RecordingHttpClientFactory(new CapturingHandler()));

        var config = runner.LoadProviderConfig("definitely-not-a-provider");

        config.Enabled.Should().BeFalse("alias normalization must not widen the allowlist");
    }

    // ── F7 — LlmProxyService descriptor wiring validates at startup ─────────

    [Test]
    public void LlmProxyService_ValidateProviderWiring_DoesNotThrow()
    {
        // Called from AddSaaSServices at boot; a catalogue regression would
        // surface here as the intended InvalidOperationException, not as a
        // TypeInitializationException at first proxied request.
        FluentActions.Invoking(LlmProxyService.ValidateProviderWiring).Should().NotThrow();
    }

    // ── Story 46-0 (AC8) — models-URL egress pins ───────────────────────────
    //
    // The drift guard for the epic 46 wire-facts table: for every descriptor
    // with a non-null ModelsEndpointPath, the path-preserving join of its
    // DefaultBaseUrl + ModelsEndpointPath must equal the DOCUMENTED absolute
    // models URL (groq's /openai and openrouter's /api base segments
    // preserved; deepseek's un-prefixed /models; gemini's /v1beta/openai).

    [TestCase("anthropic", "https://api.anthropic.com/v1/models",
        Description = "verified live 2026-07-27: unauthenticated GET → 401 'x-api-key header is required'")]
    [TestCase("openai", "https://api.openai.com/v1/models")]
    [TestCase("openrouter", "https://openrouter.ai/api/v1/models",
        Description = "the /api base segment is preserved — OpenRouter's documented public models URL")]
    [TestCase("gemini", "https://generativelanguage.googleapis.com/v1beta/openai/models",
        Description = "verified live 2026-07-27: bad Bearer → 400 'Please pass a valid API key'")]
    [TestCase("google", "https://generativelanguage.googleapis.com/v1beta/openai/models")]
    [TestCase("groq", "https://api.groq.com/openai/v1/models",
        Description = "the /openai base segment is preserved")]
    [TestCase("deepseek", "https://api.deepseek.com/models",
        Description = "NO /v1 — api-docs.deepseek.com/api/list-models")]
    [TestCase("moonshot", "https://api.moonshot.ai/v1/models")]
    [TestCase("together", "https://api.together.xyz/v1/models",
        Description = "answers a BARE JSON array, not {data:[…]} — parser-owned")]
    [TestCase("local-llm", "http://localhost:11434/v1/models")]
    [TestCase("ollama", "http://localhost:11434/v1/models")]
    [TestCase("lmstudio", "http://localhost:1234/v1/models")]
    public void ModelsUrl_ComposesToTheDocumentedAbsoluteUrl(string key, string expected)
    {
        var descriptor = ProviderCatalog.Resolve(key);
        descriptor.Should().NotBeNull();
        descriptor!.ModelsEndpointPath.Should().NotBeNull(
            $"'{key}' is a listable provider per the epic 46 survey");

        ProviderCatalog.CombineUrl(descriptor.DefaultBaseUrl, descriptor.ModelsEndpointPath!)
            .Should().Be(expected);
    }

    [Test]
    public void ModelsUrl_ExactListableKeyset()
    {
        // Keyset drift guard: the set of listable providers IS the survey
        // table. z-ai (no documented route — re-checked 2026-07-27, docs.z.ai
        // unreachable), azure-openai (deployment listing / api-version
        // semantics) and github-copilot (token-exchange auth) ship null and
        // degrade to free-text model entry (epic D4).
        ProviderCatalog.HttpProviders
            .Where(d => d.ModelsEndpointPath is not null)
            .Select(d => d.Key)
            .Should().BeEquivalentTo(new[]
            {
                "anthropic", "openai", "openrouter", "gemini", "google", "groq",
                "deepseek", "moonshot", "together", "local-llm", "ollama", "lmstudio",
            });

        ProviderCatalog.HttpProviders
            .Where(d => d.ModelsEndpointPath is null)
            .Select(d => d.Key)
            .Should().BeEquivalentTo(new[] { "z-ai", "azure-openai", "github-copilot" });
    }

    [Test]
    public void ModelsPathForBase_AppliesTheF3OverrideRule()
    {
        var gemini = ProviderCatalog.Resolve("gemini")!;
        var deepseek = ProviderCatalog.Resolve("deepseek")!;
        var zai = ProviderCatalog.Resolve("z-ai")!;

        // At the descriptor's own default base → the provider-specific path.
        ProviderCatalog.ModelsPathForBase(gemini, gemini.DefaultBaseUrl)
            .Should().Be("/v1beta/openai/models");
        ProviderCatalog.ModelsPathForBase(deepseek, "https://api.deepseek.com/")
            .Should().Be("/models", "slash-insensitive default-base match");

        // A config-overridden base (an OpenAI-compatible proxy) → the
        // dialect-generic /v1/models, never the provider-specific path.
        ProviderCatalog.ModelsPathForBase(gemini, "https://llm-proxy.internal/gemini")
            .Should().Be("/v1/models");
        ProviderCatalog.ModelsPathForBase(deepseek, "https://llm-proxy.internal/ds")
            .Should().Be("/v1/models");

        // Unlistable stays unlistable regardless of base — listing support is
        // a property of the PROVIDER, not the URL.
        ProviderCatalog.ModelsPathForBase(zai, zai.DefaultBaseUrl).Should().BeNull();
        ProviderCatalog.ModelsPathForBase(zai, "https://llm-proxy.internal/zai").Should().BeNull();
        ProviderCatalog.ModelsPathForBase(null, "https://anything").Should().BeNull();
    }
}
