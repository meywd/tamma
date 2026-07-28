using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.Tests.LlmCall; // FakeTimeProvider (local test helper)
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Providers;
using Tamma.Core;

namespace Tamma.Activities.Tests.Providers;

/// <summary>
/// Story 46-0 (AC2/AC3/AC5/AC7/AC9) — the live model-listing seam:
/// the two-shape parser (golden JSON fixtures per surveyed provider), the
/// 5-minute per-(provider, tenant) cache, fail-soft stale/empty envelopes,
/// the key-optional listing allowlist, the per-call fetch composition
/// (URL + auth headers pinned through the REAL service), current-model
/// injection, and DTO key-material hygiene.
/// </summary>
[TestFixture]
public class ProviderModelCatalogTests
{
    private const string Sentinel = "SENTINEL-MODELS-KEY";

    // ── harness ─────────────────────────────────────────────────────────────

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<HttpRequestMessage> Requests { get; } = new();

        public void EnqueueJson(string json) =>
            _responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        public void EnqueueStatus(HttpStatusCode status) =>
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_responses.Count == 0)
            {
                throw new HttpRequestException("no scripted response");
            }
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        private readonly CapturingHandler _handler;
        public RecordingHttpClientFactory(CapturingHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class FakeResolver : IProviderCredentialResolver
    {
        private readonly string? _key;
        public List<(Guid? TenantId, string Provider)> Calls { get; } = new();

        /// <param name="key">null ⇒ throw PROVIDER_CREDENTIAL_UNAVAILABLE.</param>
        public FakeResolver(string? key) => _key = key;

        public Task<ProviderCredential> ResolveAsync(
            Guid? tenantId, string providerName, CancellationToken ct = default)
        {
            Calls.Add((tenantId, providerName));
            if (_key is null)
            {
                throw new TammaError(
                    "PROVIDER_CREDENTIAL_UNAVAILABLE", "no key",
                    retryable: false, severity: TammaErrorSeverity.High);
            }
            return Task.FromResult(new ProviderCredential(
                _key, CredentialSource.Platform, "platform:test", null));
        }

        public void Invalidate(Guid? tenantId, string providerName) { }
    }

    private static ProviderModelCatalogService Service(
        CapturingHandler handler, IProviderCredentialResolver resolver,
        FakeTimeProvider? time = null) =>
        new(new RecordingHttpClientFactory(handler), resolver,
            NullLogger<ProviderModelCatalogService>.Instance, time);

    // ── golden fixtures (shapes from the epic 46 survey, 2026-07-27) ────────

    private const string OpenAiFixture = """
        {"object":"list","data":[
          {"id":"gpt-4o","object":"model","created":1715367049,"owned_by":"system"},
          {"id":"gpt-4o-mini","object":"model","created":1721172741,"owned_by":"system"}]}
        """;

    private const string AnthropicFixture = """
        {"data":[
          {"type":"model","id":"claude-sonnet-4-5","display_name":"Claude Sonnet 4.5","created_at":"2025-09-29T00:00:00Z"},
          {"type":"model","id":"claude-opus-4-7","display_name":"Claude Opus 4.7","created_at":"2026-01-15T00:00:00Z"}],
         "has_more":false,"first_id":"claude-sonnet-4-5","last_id":"claude-opus-4-7"}
        """;

    private const string TogetherBareArrayFixture = """
        [{"id":"meta-llama/Llama-3.3-70B-Instruct-Turbo","object":"model","display_name":"Llama 3.3 70B Instruct Turbo","organization":"Meta"},
         {"id":"Qwen/Qwen2.5-72B-Instruct-Turbo","object":"model","display_name":"Qwen2.5 72B Instruct Turbo","organization":"Qwen"}]
        """;

    private const string OpenRouterFixture = """
        {"data":[
          {"id":"anthropic/claude-sonnet-4.5","name":"Anthropic: Claude Sonnet 4.5","created":1727654400,"pricing":{"prompt":"0.000003"}},
          {"id":"deepseek/deepseek-chat","name":"DeepSeek V3","created":1735257600,"pricing":{"prompt":"0.00000014"}}]}
        """;

    private const string GeminiFixture = """
        {"object":"list","data":[
          {"id":"models/gemini-2.0-flash","object":"model","owned_by":"google"},
          {"id":"models/gemini-2.5-pro","object":"model","owned_by":"google"}]}
        """;

    // ── AC9 tests 1–6: the two-shape parser ────────────────────────────────

    [Test]
    public async Task Parser_OpenAiEnvelope_IdsWithNullDisplayNames()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson(OpenAiFixture);
        var service = Service(handler, new FakeResolver("k"));

        var list = await service.ListModelsAsync("openai", null);

        list.ErrorCode.Should().BeNull();
        list.Stale.Should().BeFalse();
        list.Models.Select(m => m.Id).Should().Equal("gpt-4o", "gpt-4o-mini");
        list.Models.Should().OnlyContain(m => m.DisplayName == null,
            "the OpenAI list has no display-name field — the id IS the name");
        list.Models.Should().OnlyContain(m => !m.Deprecated);
    }

    [Test]
    public async Task Parser_AnthropicEnvelope_MapsDisplayName()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson(AnthropicFixture);
        var service = Service(handler, new FakeResolver("k"));

        var list = await service.ListModelsAsync("anthropic", null);

        list.Models.Select(m => (m.Id, m.DisplayName)).Should().Equal(
            ("claude-sonnet-4-5", "Claude Sonnet 4.5"),
            ("claude-opus-4-7", "Claude Opus 4.7"));
    }

    [Test]
    public async Task Parser_TogetherBareArray_ParsedWithDisplayNames()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson(TogetherBareArrayFixture);
        var service = Service(handler, new FakeResolver("k"));

        var list = await service.ListModelsAsync("together", null);

        list.ErrorCode.Should().BeNull(
            "Together's BARE-ARRAY envelope is one of the two shapes the parser owns");
        list.Models.Select(m => m.Id).Should().Equal(
            "meta-llama/Llama-3.3-70B-Instruct-Turbo", "Qwen/Qwen2.5-72B-Instruct-Turbo");
        list.Models[0].DisplayName.Should().Be("Llama 3.3 70B Instruct Turbo");
    }

    [Test]
    public async Task Parser_OpenRouter_NameIsTheDisplayName()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson(OpenRouterFixture);
        var service = Service(handler, new FakeResolver("k"));

        var list = await service.ListModelsAsync("openrouter", null);

        list.Models[0].Id.Should().Be("anthropic/claude-sonnet-4.5");
        list.Models[0].DisplayName.Should().Be("Anthropic: Claude Sonnet 4.5");
    }

    [Test]
    public async Task Parser_Gemini_ModelsPrefixedIdsKeptVerbatim()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson(GeminiFixture);
        var service = Service(handler, new FakeResolver("k"));

        var list = await service.ListModelsAsync("gemini", null);

        list.Models.Select(m => m.Id).Should().Equal(
            "models/gemini-2.0-flash", "models/gemini-2.5-pro");
    }

    [Test]
    public void Parser_EntryWithoutId_SkippedNotFatal()
    {
        using var doc = JsonDocument.Parse("""
            {"data":[{"id":"good-model"},{"object":"model"},{"id":42},{"id":""},{"id":"another"}]}
            """);
        var models = ProviderModelCatalogService.ParseModels(doc.RootElement.Clone());
        models.Select(m => m.Id).Should().Equal("good-model", "another");
    }

    [Test]
    public void Parser_DeprecatedFlag_ReadWhenPresent()
    {
        using var doc = JsonDocument.Parse("""
            {"data":[{"id":"old-model","deprecated":true},{"id":"new-model","deprecated":false},{"id":"plain"}]}
            """);
        var models = ProviderModelCatalogService.ParseModels(doc.RootElement.Clone());
        models.Single(m => m.Id == "old-model").Deprecated.Should().BeTrue();
        models.Where(m => m.Id != "old-model").Should().OnlyContain(m => !m.Deprecated);
    }

    // ── fetch composition: URL + per-call headers on the REAL service ──────

    [Test]
    public async Task Fetch_Anthropic_UsesDocumentedUrlAndApiKeyPlusVersionHeaders()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson(AnthropicFixture);
        var service = Service(handler, new FakeResolver(Sentinel));

        await service.ListModelsAsync("anthropic", null);

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri.Should().Be(new Uri("https://api.anthropic.com/v1/models"));
        request.Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be(Sentinel);
        request.Headers.GetValues("anthropic-version").Should().ContainSingle()
            .Which.Should().Be("2023-06-01");
        request.Headers.Contains("Authorization").Should().BeFalse();
    }

    [Test]
    public async Task Fetch_Groq_PreservesBasePathAndSendsBearer()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson(OpenAiFixture);
        var service = Service(handler, new FakeResolver(Sentinel));

        await service.ListModelsAsync("groq", null);

        var request = handler.Requests.Should().ContainSingle().Which;
        request.RequestUri.Should().Be(new Uri("https://api.groq.com/openai/v1/models"));
        request.Headers.GetValues("Authorization").Should().ContainSingle()
            .Which.Should().Be($"Bearer {Sentinel}");
        request.Headers.Contains("x-api-key").Should().BeFalse();
    }

    [Test]
    public async Task Fetch_Alias_ResolvesToCanonicalProvider()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson(OpenAiFixture);
        var resolver = new FakeResolver("k");
        var service = Service(handler, resolver);

        var list = await service.ListModelsAsync("kimi", null);

        list.ErrorCode.Should().BeNull();
        resolver.Calls.Should().ContainSingle().Which.Provider.Should().Be("moonshot");
        handler.Requests[0].RequestUri.Should().Be(new Uri("https://api.moonshot.ai/v1/models"));
    }

    // ── unlistable providers ────────────────────────────────────────────────

    [TestCase("z-ai")]
    [TestCase("azure-openai")]
    [TestCase("github-copilot")]
    [TestCase("opencode")]
    [TestCase("zen-mcp")]
    [TestCase("definitely-not-a-provider")]
    public async Task Unlistable_ReturnsModelsNotSupported_NoHttp(string key)
    {
        var handler = new CapturingHandler();
        var resolver = new FakeResolver("k");
        var service = Service(handler, resolver);

        var list = await service.ListModelsAsync(key, null);

        list.Models.Should().BeEmpty();
        list.ErrorCode.Should().Be("models_not_supported");
        handler.Requests.Should().BeEmpty("no HTTP attempt for an unlistable provider");
        resolver.Calls.Should().BeEmpty("no credential resolution either");
    }

    // ── AC9 tests 7–8: cache TTL + tenant isolation ─────────────────────────

    [Test]
    public async Task Cache_SecondCallWithinTtl_NoSecondHttpRequest()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-27T12:00:00Z"));
        var handler = new CapturingHandler();
        handler.EnqueueJson(OpenAiFixture);
        var service = Service(handler, new FakeResolver("k"), time);

        var first = await service.ListModelsAsync("openai", null);
        var second = await service.ListModelsAsync("openai", null);

        handler.Requests.Should().HaveCount(1, "the second call within the 5-min TTL is a cache hit");
        second.Models.Should().BeEquivalentTo(first.Models);
        second.Stale.Should().BeFalse();

        // TTL expiry → refetch.
        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        handler.EnqueueJson(OpenAiFixture);
        await service.ListModelsAsync("openai", null);
        handler.Requests.Should().HaveCount(2, "the TTL is 5 minutes");
    }

    [Test]
    public void CacheTtl_IsFiveMinutes()
    {
        ProviderModelCatalogService.CacheTtl.Should().Be(TimeSpan.FromMinutes(5),
            "AC3 pins the 5-minute cache");
    }

    [Test]
    public async Task Cache_TenantIsolation_PerTenantKeys()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson(OpenAiFixture);   // tenant A
        handler.EnqueueJson(AnthropicFixture); // tenant B — different payload
        handler.EnqueueJson(GeminiFixture);    // platform (null)
        var service = Service(handler, new FakeResolver("k"));
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var a = await service.ListModelsAsync("openai", tenantA);
        var b = await service.ListModelsAsync("openai", tenantB);
        var p = await service.ListModelsAsync("openai", null);

        handler.Requests.Should().HaveCount(3,
            "each (provider, tenant) key fetches separately — a BYOK-filtered list must never leak");
        a.Models.Select(m => m.Id).Should().Contain("gpt-4o");
        b.Models.Select(m => m.Id).Should().Contain("claude-sonnet-4-5");
        p.Models.Select(m => m.Id).Should().Contain("models/gemini-2.0-flash");

        // Cache hits stay isolated too.
        var aAgain = await service.ListModelsAsync("openai", tenantA);
        handler.Requests.Should().HaveCount(3);
        aAgain.Models.Select(m => m.Id).Should().Contain("gpt-4o");
    }

    // ── review F12: BYOK-change invalidation hook ──────────────────────────

    [Test]
    public async Task Invalidate_EvictsTheTenantsCachedList_OtherTenantsUnaffected()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson(OpenAiFixture);   // tenant A, first fetch
        handler.EnqueueJson(AnthropicFixture); // tenant B, first fetch
        handler.EnqueueJson(GeminiFixture);    // tenant A, refetch after invalidation
        var service = Service(handler, new FakeResolver("k"));
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await service.ListModelsAsync("openai", tenantA);
        await service.ListModelsAsync("openai", tenantB);
        handler.Requests.Should().HaveCount(2);

        // A BYOK change for tenant A evicts ONLY tenant A's entry…
        service.Invalidate("openai", tenantA);

        var refetched = await service.ListModelsAsync("openai", tenantA);
        handler.Requests.Should().HaveCount(3,
            "the invalidated (provider, tenant) entry must refetch under the NEW credential");
        refetched.Models.Select(m => m.Id).Should().Contain("models/gemini-2.0-flash",
            "the refetch result replaced the pre-invalidation cache entry");

        // …tenant B (and its cache hit) is untouched.
        var b = await service.ListModelsAsync("openai", tenantB);
        handler.Requests.Should().HaveCount(3, "tenant B still serves from its cache");
        b.Models.Select(m => m.Id).Should().Contain("claude-sonnet-4-5");
    }

    [Test]
    public async Task Invalidate_AcceptsAliases_AndUnknownKeysAreANoOp()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson(OpenAiFixture);
        handler.EnqueueJson(OpenAiFixture);
        var service = Service(handler, new FakeResolver("k"));
        var tenant = Guid.NewGuid();

        await service.ListModelsAsync("moonshot", tenant);
        handler.Requests.Should().HaveCount(1);

        service.Invalidate("kimi", tenant); // alias → canonical moonshot entry

        await service.ListModelsAsync("moonshot", tenant);
        handler.Requests.Should().HaveCount(2, "the alias evicted the canonical cache entry");

        var act = () => service.Invalidate("definitely-not-a-provider", tenant);
        act.Should().NotThrow("unknown keys are a harmless no-op");
    }

    // ── AC9 tests 9–10: fail-soft stale / empty ─────────────────────────────

    [Test]
    public async Task FailSoft_FetchFails_CacheWarm_ServesStaleFlagged()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-27T12:00:00Z"));
        var handler = new CapturingHandler();
        handler.EnqueueJson(OpenAiFixture);
        var service = Service(handler, new FakeResolver("k"), time);

        var fresh = await service.ListModelsAsync("openai", null);
        fresh.Stale.Should().BeFalse();

        time.Advance(TimeSpan.FromMinutes(6));
        handler.EnqueueStatus(HttpStatusCode.InternalServerError);
        var stale = await service.ListModelsAsync("openai", null);

        stale.Stale.Should().BeTrue("the last-known-good list is served, clearly flagged");
        stale.ErrorCode.Should().Be("fetch_failed");
        stale.Models.Should().BeEquivalentTo(fresh.Models);
        stale.FetchedAt.Should().Be(fresh.FetchedAt, "the stale list carries ITS fetch time");
    }

    [Test]
    public async Task FailSoft_FetchFails_CacheCold_EmptyPlusErrorCode()
    {
        var handler = new CapturingHandler();
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        var service = Service(handler, new FakeResolver("k"));

        var list = await service.ListModelsAsync("openai", null);

        list.Models.Should().BeEmpty();
        list.Stale.Should().BeFalse();
        list.ErrorCode.Should().Be("fetch_failed");
    }

    [Test]
    public async Task FailSoft_UnparseableBody_ParseFailedCode()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson("this is not json");
        var service = Service(handler, new FakeResolver("k"));

        var list = await service.ListModelsAsync("openai", null);

        list.Models.Should().BeEmpty();
        list.ErrorCode.Should().Be("parse_failed");
    }

    // ── AC9 tests 11–12: the key-optional listing allowlist ────────────────

    [Test]
    public async Task CredentialUnavailable_NonOptionalProvider_NoHttpAttempt()
    {
        var handler = new CapturingHandler();
        var service = Service(handler, new FakeResolver(key: null)); // resolver throws

        var list = await service.ListModelsAsync("anthropic", null);

        list.Models.Should().BeEmpty();
        list.ErrorCode.Should().Be("credential_unavailable");
        handler.Requests.Should().BeEmpty(
            "an unauthenticated call would 401 and burn the timeout — short-circuit instead");
    }

    [Test]
    public async Task CredentialUnavailable_OpenRouter_UnauthenticatedFetchProceeds()
    {
        var handler = new CapturingHandler();
        handler.EnqueueJson(OpenRouterFixture);
        var service = Service(handler, new FakeResolver(key: null)); // resolver throws

        var list = await service.ListModelsAsync("openrouter", null);

        list.ErrorCode.Should().BeNull("OpenRouter's models API is public");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.Contains("Authorization").Should().BeFalse(
            "the downgrade is a genuinely unauthenticated fetch");
        handler.Requests[0].RequestUri.Should().Be(new Uri("https://openrouter.ai/api/v1/models"));
    }

    [Test]
    public void KeyOptionalAllowlist_IsExactlyTheSurveySet()
    {
        ProviderModelCatalogService.KeyOptionalProviders
            .Should().BeEquivalentTo(new[] { "openrouter", "local-llm", "ollama", "lmstudio" });
    }

    // ── AC9 test 13: current-model injection (endpoint shaping) ─────────────

    [Test]
    public void CurrentModelInjection_DelistedModel_SynthesizedAndFlagged()
    {
        var list = new ProviderModelList(
            new[]
            {
                new ProviderModelInfo("gpt-4o", null, false),
                new ProviderModelInfo("gpt-4o-mini", null, false),
            },
            DateTimeOffset.UtcNow, Stale: false, ErrorCode: null);

        var response = ProviderAdminEndpoints.BuildModelsResponse(
            "openai", list, currentModel: "gpt-4-turbo-delisted");

        response.Models.Should().HaveCount(3);
        var current = response.Models.Single(m => m.Current);
        current.Id.Should().Be("gpt-4-turbo-delisted");
        current.DisplayName.Should().BeNull("synthesized entries carry no display name");
        response.Models[0].Should().Be(current, "the synthesized current model is prepended");
        current.Delisted.Should().BeTrue(
            "the envelope states the fact — the UIs no longer infer synthesis positionally " +
            "(bug 2026-07-27-models-envelope-lacks-delisted-flag)");
        response.Models.Where(m => !m.Current).Should().OnlyContain(
            m => !m.Delisted, "only the synthesized entry carries the flag");
    }

    [Test]
    public void CurrentModelInjection_ListedFirstEntryWithoutDisplayName_NotDelisted()
    {
        // The 46-2 heuristic's documented false positive: a display-name-less
        // provider (OpenAI/Groq/DeepSeek-style list) whose current model
        // genuinely IS the first entry. Structurally identical to a synthesized
        // pin — the flag is what tells them apart.
        var list = new ProviderModelList(
            new[]
            {
                new ProviderModelInfo("gpt-4o", null, false),
                new ProviderModelInfo("gpt-4o-mini", null, false),
            },
            DateTimeOffset.UtcNow, Stale: false, ErrorCode: null);

        var response = ProviderAdminEndpoints.BuildModelsResponse("openai", list, "gpt-4o");

        response.Models.Should().HaveCount(2, "no synthesis happened");
        var current = response.Models.Single(m => m.Current);
        current.Should().Be(response.Models[0]);
        current.DisplayName.Should().BeNull();
        current.Delisted.Should().BeFalse("a listed-in-place current entry never carries the flag");
    }

    [Test]
    public void DelistedFlag_OnTheWire_PresentTrueOnlyOnTheSynthesizedEntry()
    {
        // Wire contract the TS clients type as `delisted?: boolean`:
        // `"delisted":true` on the synthesized entry; OMITTED (WhenWritingDefault)
        // on genuinely-listed entries — absent/false both read as "listed".
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var list = new ProviderModelList(
            new[] { new ProviderModelInfo("gpt-4o", null, false) },
            DateTimeOffset.UtcNow, Stale: false, ErrorCode: null);

        var synthesized = JsonSerializer.Serialize(
            ProviderAdminEndpoints.BuildModelsResponse("openai", list, "gone-model"), options);
        synthesized.Should().Contain("\"delisted\":true");
        synthesized.Should().NotContain("\"delisted\":false");

        var listed = JsonSerializer.Serialize(
            ProviderAdminEndpoints.BuildModelsResponse("openai", list, "gpt-4o"), options);
        listed.Should().NotContain("delisted", "false is omitted from the wire");
    }

    [Test]
    public void CurrentModelInjection_ListedModel_FlaggedInPlace()
    {
        var list = new ProviderModelList(
            new[]
            {
                new ProviderModelInfo("gpt-4o", null, false),
                new ProviderModelInfo("gpt-4o-mini", null, false),
            },
            DateTimeOffset.UtcNow, Stale: false, ErrorCode: null);

        var response = ProviderAdminEndpoints.BuildModelsResponse("openai", list, "gpt-4o-mini");

        response.Models.Should().HaveCount(2, "no synthesis when the list carries the model");
        response.Models.Single(m => m.Current).Id.Should().Be("gpt-4o-mini");
        response.Models.Should().OnlyContain(
            m => !m.Delisted, "no entry is flagged when the list carries the current model");
    }

    [Test]
    public void CurrentModelInjection_CaseVariantSavedModel_FlaggedInPlace_NoDelistedDuplicate()
    {
        // Review F10 — the membership check is OrdinalIgnoreCase (consistent
        // with the catalogue's case-insensitive key handling): a case-variant
        // saved model previously produced a FALSE Delisted entry prepended on
        // top of the genuinely-listed row (duplicate + wrong flag).
        var list = new ProviderModelList(
            new[]
            {
                new ProviderModelInfo("GPT-4o", null, false),
                new ProviderModelInfo("gpt-4o-mini", null, false),
            },
            DateTimeOffset.UtcNow, Stale: false, ErrorCode: null);

        var response = ProviderAdminEndpoints.BuildModelsResponse("openai", list, "gpt-4o");

        response.Models.Should().HaveCount(2,
            "no synthesized duplicate for a case-variant of a listed model");
        var current = response.Models.Single(m => m.Current);
        current.Id.Should().Be("GPT-4o", "the listed entry is flagged in place, id verbatim");
        current.Delisted.Should().BeFalse("the model IS listed — only spelled differently");
    }

    [Test]
    public void CurrentModelInjection_EmptyEffectiveModel_NothingInjected()
    {
        var list = new ProviderModelList(
            new[] { new ProviderModelInfo("m", null, false) },
            DateTimeOffset.UtcNow, false, null);

        ProviderAdminEndpoints.BuildModelsResponse("groq", list, currentModel: "")
            .Models.Should().OnlyContain(m => !m.Current);
    }

    // ── AC9 test 15 (route shaping): unknown key → 404, never enumerating ──

    [Test]
    public void NormalizeProvider_UnknownKey_404WithoutEnumeration()
    {
        var (norm, err) = ProviderAdminEndpoints.NormalizeProvider("definitely-not-a-provider");
        norm.Should().BeNull();
        err.Should().NotBeNull();
        err!.GetType().Name.Should().Contain("NotFound");
    }

    [Test]
    public void NormalizeProvider_Alias_ResolvesCanonically()
    {
        ProviderAdminEndpoints.NormalizeProvider("kimi").Provider.Should().Be("moonshot");
        ProviderAdminEndpoints.NormalizeProvider("z.ai").Provider.Should().Be("z-ai");
        ProviderAdminEndpoints.NormalizeProvider("ANTHROPIC").Provider.Should().Be("anthropic");
    }

    // ── AC9 test 17: DTO hygiene — no key-shaped members anywhere ───────────

    [Test]
    public void DtoHygiene_ResponseTypesCarryNoKeyShapedMembers()
    {
        var dtoTypes = new[]
        {
            typeof(ProviderModelInfo), typeof(ProviderModelList),
            typeof(ProviderModelEntry), typeof(ProviderModelsResponse),
            typeof(ProviderStatusRow), typeof(PutProviderSettingsResponse),
            typeof(TenantProviderRosterRow), typeof(TenantProviderModelResponse),
            typeof(PutTenantProviderModelResponse),
        };
        var forbidden = new[] { "apikey", "plaintext", "secret", "credential", "token" };

        foreach (var type in dtoTypes)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                forbidden.Should().NotContain(
                    f => property.Name.ToLowerInvariant().Contains(f),
                    $"{type.Name}.{property.Name} must not be key-shaped (46-0 AC7)");
            }
        }
    }
}
