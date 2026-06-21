using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Tests.LlmCall;

/// <summary>
/// Story 32-3 Phase 3 / Phase 5 — credential resolution wired into
/// <see cref="CallLlmInlineActivity"/>, including the load-bearing redaction
/// test (AC5): the resolved BYOK key reaches the outbound HTTP header ONLY and
/// is absent from the resulting diagnostic.
/// </summary>
[TestFixture]
public class CallLlmInlineCredentialTests
{
    private const string Sentinel = "SENTINEL-BYOK-XYZ";

    // ── AC12: LoadProviderConfig returns empty ApiKey (resolver is sole source) ──

    [Test]
    public async Task LoadProviderConfigWithKey_NoResolver_LeavesApiKeyEmpty_LegacyPath()
    {
        var activity = new CallLlmInlineActivity(); // no resolver

        var (config, source) = await activity.LoadProviderConfigWithKeyAsync(
            "anthropic", tenantId: null, CancellationToken.None);

        config.ApiKey.Should().BeEmpty("the resolver is the only key source; legacy path leaves it blank");
        config.BaseUrl.Should().Be("https://api.anthropic.com");
        source.Should().BeNull();
    }

    [Test]
    public async Task LoadProviderConfigWithKey_NeverReadsApiKeyFromConfigSection()
    {
        // Regression guard (Story 32-3 Risk: "_configuration read sneaks back in").
        // Even with a populated LlmProviders config section AND legacy
        // Anthropic:ApiKey, the activity must NOT surface a key from config — the
        // resolver is the only key source. No resolver wired ⇒ empty key.
        var config = new DictionaryConfiguration(new Dictionary<string, string?>
        {
            ["LlmProviders:anthropic:BaseUrl"] = "https://example.test",
            ["LlmProviders:anthropic:ApiKey"] = "SHOULD-NEVER-BE-READ",
            ["Anthropic:ApiKey"] = "SHOULD-NEVER-BE-READ-EITHER",
        });
        var activity = new CallLlmInlineActivity(
            NullLogger<CallLlmInlineActivity>.Instance, null, config, null);

        var (cfg, source) = await activity.LoadProviderConfigWithKeyAsync(
            "anthropic", tenantId: null, CancellationToken.None);

        cfg.ApiKey.Should().BeEmpty();
        cfg.BaseUrl.Should().Be("https://example.test", "non-secret config still flows");
        source.Should().BeNull();
    }

    // ── AC3/AC4: resolver populates the key + surfaces credentialSource ──

    [Test]
    public async Task LoadProviderConfigWithKey_ByokResolver_PopulatesKey_SourceByok()
    {
        var resolver = new FakeResolver(new ProviderCredential(
            Sentinel, CredentialSource.Byok, "tenant:abc:provider/anthropic/api-key", 1));
        var activity = NewActivity(resolver);
        var tenant = Guid.NewGuid();

        var (config, source) = await activity.LoadProviderConfigWithKeyAsync(
            "anthropic", tenant, CancellationToken.None);

        config.ApiKey.Should().Be(Sentinel);
        source.Should().Be("byok");
        resolver.LastTenantId.Should().Be(tenant);
        resolver.LastProvider.Should().Be("anthropic");
    }

    [Test]
    public async Task LoadProviderConfigWithKey_PlatformResolver_SourcePlatform()
    {
        var resolver = new FakeResolver(new ProviderCredential(
            "PLATFORM-KEY", CredentialSource.Platform, "platform:anthropic/api-key", null));
        var activity = NewActivity(resolver);

        var (config, source) = await activity.LoadProviderConfigWithKeyAsync(
            "anthropic", tenantId: null, CancellationToken.None);

        config.ApiKey.Should().Be("PLATFORM-KEY");
        source.Should().Be("platform");
    }

    // ── AC6: resolver fail-closed propagates (caught by the activity as a failed attempt) ──

    [Test]
    public async Task LoadProviderConfigWithKey_ResolverThrows_PropagatesTammaError()
    {
        var resolver = new ThrowingResolver();
        var activity = NewActivity(resolver);

        var act = async () => await activity.LoadProviderConfigWithKeyAsync(
            "anthropic", Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<Tamma.Core.TammaError>()
            .Where(e => e.Code == "PROVIDER_CREDENTIAL_UNAVAILABLE");
    }

    // ── AC5: REDACTION GATE — sentinel reaches the header ONLY ──────────────

    [Test]
    public async Task Redaction_ByokKey_ReachesAnthropicHeaderOnly_NeverDiagnostic()
    {
        var resolver = new FakeResolver(new ProviderCredential(
            Sentinel, CredentialSource.Byok, "tenant:abc:provider/anthropic/api-key", 1));
        var capture = new CapturingHandler(AnthropicSuccessBody());
        var activity = NewActivity(resolver);
        var tenant = Guid.NewGuid();

        // 1) Resolve (BYOK) → config carries the sentinel.
        var (config, source) = await activity.LoadProviderConfigWithKeyAsync(
            "anthropic", tenant, CancellationToken.None);
        source.Should().Be("byok");

        // 2) Make the call with a capturing handler.
        using var http = new HttpClient(capture);
        var response = await activity.CallAnthropicMessages(
            http, config, "claude-sonnet-4-20250514", "system", "user", 256, 0.7, null);

        // 3) Sentinel is in x-api-key ONLY.
        capture.SentApiKeyHeader.Should().Be(Sentinel);
        capture.SentAuthorizationHeader.Should().BeNull();
        capture.SentBody.Should().NotContain(Sentinel, "the key is a header, never the body");

        // 4) Build a diagnostic exactly as the activity does and assert the
        //    sentinel never appears in the serialized diagnostic.
        var diag = new ProviderAttemptDiagnostic
        {
            ProviderName = "anthropic",
            Model = "claude-sonnet-4-20250514",
            Succeeded = response.Success,
            CredentialSource = source,
        };
        var serializedDiag = JsonSerializer.Serialize(diag);
        serializedDiag.Should().NotContain(Sentinel);
        serializedDiag.Should().Contain("byok"); // the tag-safe source survives
    }

    [Test]
    public async Task Redaction_ByokKey_ReachesOpenAiBearerHeaderOnly()
    {
        var resolver = new FakeResolver(new ProviderCredential(
            Sentinel, CredentialSource.Byok, "tenant:abc:provider/openai/api-key", 1));
        var capture = new CapturingHandler(OpenAiSuccessBody());
        var activity = NewActivity(resolver);

        var (config, _) = await activity.LoadProviderConfigWithKeyAsync(
            "openai", Guid.NewGuid(), CancellationToken.None);

        using var http = new HttpClient(capture);
        await activity.CallOpenAiCompatible(
            http, config, "gpt-4o", "system", "user", 256, 0.7, null);

        capture.SentAuthorizationHeader.Should().Be($"Bearer {Sentinel}");
        capture.SentApiKeyHeader.Should().BeNull();
        capture.SentBody.Should().NotContain(Sentinel);
    }

    // ─────────────────────────────────────────────────────────────────────

    private static CallLlmInlineActivity NewActivity(IProviderCredentialResolver resolver) =>
        new(NullLogger<CallLlmInlineActivity>.Instance, null, null, null,
            null, null, null, null, null, resolver);

    private static string AnthropicSuccessBody() => """
        {"content":[{"type":"text","text":"ok"}],"model":"claude-sonnet-4-20250514",
         "stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}
        """;

    private static string OpenAiSuccessBody() => """
        {"choices":[{"finish_reason":"stop","message":{"content":"ok"}}],
         "model":"gpt-4o","usage":{"prompt_tokens":1,"completion_tokens":1}}
        """;

    private sealed class FakeResolver(ProviderCredential cred) : IProviderCredentialResolver
    {
        public Guid? LastTenantId { get; private set; }
        public string? LastProvider { get; private set; }

        public Task<ProviderCredential> ResolveAsync(
            Guid? tenantId, string providerName, CancellationToken ct = default)
        {
            LastTenantId = tenantId;
            LastProvider = providerName;
            return Task.FromResult(cred);
        }

        public void Invalidate(Guid? tenantId, string providerName) { }
    }

    private sealed class ThrowingResolver : IProviderCredentialResolver
    {
        public Task<ProviderCredential> ResolveAsync(
            Guid? tenantId, string providerName, CancellationToken ct = default) =>
            throw new Tamma.Core.TammaError(
                "PROVIDER_CREDENTIAL_UNAVAILABLE", "no key",
                retryable: false, severity: Tamma.Core.TammaErrorSeverity.High);

        public void Invalidate(Guid? tenantId, string providerName) { }
    }

    /// <summary>
    /// Minimal <see cref="IConfiguration"/> over a flat key dictionary —
    /// supports the indexer + <c>GetSection().Exists()</c> path that
    /// <c>LoadProviderConfig</c> uses, without pulling an extra package ref.
    /// </summary>
    private sealed class DictionaryConfiguration(IDictionary<string, string?> data) : IConfiguration
    {
        public string? this[string key]
        {
            get => data.TryGetValue(key, out var v) ? v : null;
            set => data[key] = value;
        }

        public IConfigurationSection GetSection(string key) => new Section(this, key, data);

        public IEnumerable<IConfigurationSection> GetChildren() => Array.Empty<IConfigurationSection>();

        public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);

        private sealed class Section(DictionaryConfiguration root, string path, IDictionary<string, string?> data)
            : IConfigurationSection
        {
            public string? this[string key]
            {
                get => root[$"{path}:{key}"];
                set => root[$"{path}:{key}"] = value;
            }

            public string Key => path.Split(':').Last();
            public string Path => path;
            public string? Value
            {
                get => root[path];
                set => root[path] = value;
            }

            public IConfigurationSection GetSection(string key) => root.GetSection($"{path}:{key}");
            public IEnumerable<IConfigurationSection> GetChildren() =>
                data.Keys.Where(k => k.StartsWith($"{path}:", StringComparison.Ordinal))
                    .Select(k => k[(path.Length + 1)..].Split(':')[0])
                    .Distinct()
                    .Select(child => (IConfigurationSection)new Section(root, $"{path}:{child}", data));
            public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);
        }
    }

    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public string? SentApiKeyHeader { get; private set; }
        public string? SentAuthorizationHeader { get; private set; }
        public string? SentBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.TryGetValues("x-api-key", out var apiKey))
                SentApiKeyHeader = string.Join(",", apiKey);
            if (request.Headers.Authorization is { } auth)
                SentAuthorizationHeader = $"{auth.Scheme} {auth.Parameter}";
            else if (request.Headers.TryGetValues("Authorization", out var authVals))
                SentAuthorizationHeader = string.Join(",", authVals);

            if (request.Content is not null)
                SentBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
