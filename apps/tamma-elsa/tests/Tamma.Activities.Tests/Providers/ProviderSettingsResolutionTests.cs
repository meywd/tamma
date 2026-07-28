using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.SaaS;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Tests.Providers;

/// <summary>
/// Story 46-1 (AC3/AC4/AC9) — the four-step default-model precedence
/// (tenant/user override → platform DB → config → descriptor) as implemented
/// ONCE in <see cref="InlineToolLoopRunner.ResolveDefaultModel"/>, its
/// provenance surface, the <see cref="LlmProxyService"/> pickup, and the
/// no-row byte-identity golden comparison (a store with no rows must resolve
/// EXACTLY like no store at all — the pre-46-1 behaviour — for every
/// catalogue key).
/// </summary>
[TestFixture]
public class ProviderSettingsResolutionTests
{
    // ── fakes ───────────────────────────────────────────────────────────────

    private sealed class FakeSettingsStore : IProviderSettingsStore
    {
        public Dictionary<(string Provider, Guid Tenant), string> TenantModels { get; } = new();
        public Dictionary<string, string> UserModels { get; } =
            new(StringComparer.OrdinalIgnoreCase); // single-user leg (unused in SaaS-shaped tests)
        public Dictionary<string, string> PlatformModels { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool> EnabledFlags { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public bool SingleUserMode { get; init; }

        public string? TryGetModel(string providerKey, Guid? tenantId)
        {
            if (SingleUserMode)
            {
                return UserModels.TryGetValue(Canonical(providerKey), out var um) ? um : null;
            }
            return tenantId is Guid tid
                && TenantModels.TryGetValue((Canonical(providerKey), tid), out var m)
                ? m
                : null;
        }

        public string? TryGetPlatformModel(string providerKey) =>
            PlatformModels.TryGetValue(Canonical(providerKey), out var m) ? m : null;

        public bool IsEnabled(string providerKey) =>
            !EnabledFlags.TryGetValue(Canonical(providerKey), out var e) || e;

        public bool HasOverride(string providerKey, Guid? tenantId) =>
            TryGetModel(providerKey, tenantId) is not null;

        public Task SetPlatformModelAsync(string p, string m, Guid? u, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetEnabledAsync(string p, bool e, Guid? u, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> RemovePlatformAsync(string p, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetPrincipalModelAsync(string p, Guid? t, Guid? u, string m, Guid? by, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> RemovePrincipalModelAsync(string p, Guid? t, Guid? u, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;

        private static string Canonical(string key) =>
            ProviderCatalog.Resolve(key)?.Key ?? key;
    }

    /// <summary>A store whose ANY read throws — proves a code path never
    /// consults the settings layer.</summary>
    private sealed class ThrowingSettingsStore : IProviderSettingsStore
    {
        public string? TryGetModel(string p, Guid? t) => throw new InvalidOperationException("consulted!");
        public string? TryGetPlatformModel(string p) => throw new InvalidOperationException("consulted!");
        public bool IsEnabled(string p) => throw new InvalidOperationException("consulted!");
        public bool HasOverride(string p, Guid? t) => throw new InvalidOperationException("consulted!");
        public Task SetPlatformModelAsync(string p, string m, Guid? u, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetEnabledAsync(string p, bool e, Guid? u, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemovePlatformAsync(string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetPrincipalModelAsync(string p, Guid? t, Guid? u, string m, Guid? by, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemovePrincipalModelAsync(string p, Guid? t, Guid? u, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static IConfiguration Config(IDictionary<string, string?>? values = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>())
            .Build();

    private static InlineToolLoopRunner Runner(
        IConfiguration? configuration = null, IProviderSettingsStore? store = null) =>
        new(logger: null, httpClientFactory: null, configuration: configuration,
            sanitizer: null, settingsStore: store);

    // ── AC9 test 1: the precedence matrix ──────────────────────────────────
    //
    // Layers for provider `openai` (descriptor default gpt-4o):
    //   T = tenant row "tenant-model", P = platform row "platform-model",
    //   C = LlmProviders:openai:DefaultModel "cfg-model", D = descriptor.
    // Crossed with tenant context present/absent (the tenant leg only exists
    // when the call carries a tenant id).

    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [TestCase(true, true, true, true, "tenant-model", "tenant-override")]
    [TestCase(true, true, true, false, "tenant-model", "tenant-override")]
    [TestCase(true, true, false, true, "tenant-model", "tenant-override")]
    [TestCase(true, true, false, false, "tenant-model", "tenant-override")]
    [TestCase(true, false, true, true, "platform-model", "platform-db")]
    [TestCase(true, false, true, false, "platform-model", "platform-db")]
    [TestCase(true, false, false, true, "cfg-model", "config")]
    [TestCase(true, false, false, false, "gpt-4o", "descriptor")]
    [TestCase(false, true, true, true, "platform-model", "platform-db",
        Description = "no tenant context ⇒ the tenant row is invisible")]
    [TestCase(false, true, true, false, "platform-model", "platform-db")]
    [TestCase(false, true, false, true, "cfg-model", "config")]
    [TestCase(false, true, false, false, "gpt-4o", "descriptor")]
    [TestCase(false, false, true, true, "platform-model", "platform-db")]
    [TestCase(false, false, true, false, "platform-model", "platform-db")]
    [TestCase(false, false, false, true, "cfg-model", "config")]
    [TestCase(false, false, false, false, "gpt-4o", "descriptor")]
    public void PrecedenceMatrix_TenantOverPlatformOverConfigOverDescriptor(
        bool tenantContext, bool tenantRow, bool platformRow, bool configPresent,
        string expectedModel, string expectedSource)
    {
        var store = new FakeSettingsStore();
        if (tenantRow) store.TenantModels[("openai", Tenant)] = "tenant-model";
        if (platformRow) store.PlatformModels["openai"] = "platform-model";
        var config = Config(configPresent
            ? new Dictionary<string, string?> { ["LlmProviders:openai:DefaultModel"] = "cfg-model" }
            : null);
        var runner = Runner(config, store);
        var tenantId = tenantContext ? Tenant : (Guid?)null;

        runner.GetDefaultModel("openai", tenantId).Should().Be(expectedModel);
        var resolution = runner.ResolveDefaultModelWithSource("openai", tenantId);
        resolution.Model.Should().Be(expectedModel);
        resolution.Source.Should().Be(expectedSource);

        // The full config load agrees (the egress path reads this).
        runner.LoadProviderConfig("openai", tenantId).DefaultModel.Should().Be(expectedModel);
    }

    // ── AC9 test 2: the early-return regression ─────────────────────────────

    [Test]
    public void ConfigSectionExists_AndPlatformRowExists_DbWins()
    {
        // The pre-46-1 shape early-returned as soon as the LlmProviders
        // section existed — a DB row could never win. This is THE regression
        // the restructure exists to fix (epic D2: config silently outranking
        // the UI makes the UI a lie).
        var store = new FakeSettingsStore { PlatformModels = { ["anthropic"] = "db-chosen-model" } };
        var config = Config(new Dictionary<string, string?>
        {
            ["LlmProviders:anthropic:BaseUrl"] = "https://config.example",
            ["LlmProviders:anthropic:DefaultModel"] = "config-chosen-model",
            ["LlmProviders:anthropic:TimeoutSeconds"] = "42",
        });
        var runner = Runner(config, store);

        var cfg = runner.LoadProviderConfig("anthropic", null);

        cfg.DefaultModel.Should().Be("db-chosen-model", "the DB layer sits ABOVE config");
        cfg.BaseUrl.Should().Be("https://config.example", "BaseUrl resolution is unchanged");
        cfg.TimeoutSeconds.Should().Be(42, "Timeout resolution is unchanged");
    }

    // ── AC9 test 3: the Anthropic:Model legacy case ─────────────────────────

    [Test]
    public void AnthropicModelLegacyKey_StillHonoured_AtTheConfigStep_AnthropicOnly()
    {
        var config = Config(new Dictionary<string, string?> { ["Anthropic:Model"] = "legacy-pinned" });
        var runner = Runner(config, new FakeSettingsStore());

        runner.GetDefaultModel("anthropic").Should().Be("legacy-pinned");
        runner.ResolveDefaultModelWithSource("anthropic", null).Source.Should().Be("config");

        // …and it is anthropic-ONLY (other providers never read it).
        runner.GetDefaultModel("openai").Should().Be("gpt-4o");

        // …and a platform DB row still outranks it.
        var store = new FakeSettingsStore { PlatformModels = { ["anthropic"] = "db-model" } };
        Runner(config, store).GetDefaultModel("anthropic").Should().Be("db-model");
    }

    // ── AC9 test 4: empty-string config stays "no opinion" (legacy shape) ──

    [Test]
    public void EmptyConfigModel_KeepsTodaysBehaviour()
    {
        // Section exists but carries no DefaultModel → the pre-46-1 code
        // returned "" (NO descriptor fallback — "caller must specify"). That
        // exact shape is preserved for no-row installs…
        var config = Config(new Dictionary<string, string?>
        {
            ["LlmProviders:openai:BaseUrl"] = "https://cfg.example",
        });
        Runner(config, new FakeSettingsStore()).GetDefaultModel("openai").Should().Be("");

        // …while a DB row (never empty — validated on write) still wins.
        var store = new FakeSettingsStore { PlatformModels = { ["openai"] = "db-model" } };
        Runner(config, store).GetDefaultModel("openai").Should().Be("db-model");
    }

    // ── AC9 test 16: no-row installs are byte-identical to pre-46-1 ────────

    [Test]
    public void NoRowInstall_ResolvesIdenticallyToNoStoreAtAll_ForEveryCatalogueKey()
    {
        // Representative config mirroring the shipped appsettings examples
        // (three providers with sections) — the rest resolve via descriptors.
        var configValues = new Dictionary<string, string?>
        {
            ["LlmProviders:anthropic:BaseUrl"] = "https://api.anthropic.com",
            ["LlmProviders:anthropic:DefaultModel"] = "claude-sonnet-4-5",
            ["LlmProviders:anthropic:TimeoutSeconds"] = "120",
            ["LlmProviders:openai:BaseUrl"] = "https://api.openai.com",
            ["LlmProviders:openai:DefaultModel"] = "gpt-4o",
            ["LlmProviders:openrouter:BaseUrl"] = "https://openrouter.ai/api",
            ["LlmProviders:openrouter:DefaultModel"] = "anthropic/claude-sonnet-4.5",
            ["Anthropic:Model"] = "should-not-matter-when-section-exists",
        };
        var withoutStore = Runner(Config(configValues), store: null);
        var withEmptyStore = Runner(Config(configValues), new FakeSettingsStore());

        var keys = ProviderCatalog.HttpProviders.Select(d => d.Key)
            .Concat(ProviderCatalog.HttpProviders.SelectMany(d => d.Aliases))
            .Concat(new[] { "opencode", "zen-mcp", "not-a-provider" });

        foreach (var key in keys)
        {
            foreach (var tenantId in new Guid?[] { null, Guid.NewGuid() })
            {
                var expected = withoutStore.LoadProviderConfig(key, tenantId);
                var actual = withEmptyStore.LoadProviderConfig(key, tenantId);

                actual.Name.Should().Be(expected.Name, $"key {key}");
                actual.BaseUrl.Should().Be(expected.BaseUrl, $"key {key}");
                actual.DefaultModel.Should().Be(expected.DefaultModel, $"key {key}");
                actual.TimeoutSeconds.Should().Be(expected.TimeoutSeconds, $"key {key}");
                actual.Enabled.Should().Be(expected.Enabled, $"key {key}");
            }
        }
    }

    [Test]
    public void PlatformScopeOverload_DelegatesWithNullTenant()
    {
        var store = new FakeSettingsStore
        {
            TenantModels = { [("openai", Tenant)] = "tenant-model" },
            PlatformModels = { ["openai"] = "platform-model" },
        };
        var runner = Runner(Config(), store);

        runner.GetDefaultModel("openai").Should().Be(
            "platform-model", "the legacy sync overload is platform-scope (no tenant leg)");
    }

    [Test]
    public void SingleUserStore_UserRow_SurfacesAsTenantOverrideSource()
    {
        var store = new FakeSettingsStore
        {
            SingleUserMode = true,
            UserModels = { ["anthropic"] = "sole-user-model" },
        };
        var runner = Runner(Config(), store);

        // In single-user mode the store maps ANY tenant argument to the sole
        // user's row (plan D3) — the runner's chain is unchanged.
        runner.GetDefaultModel("anthropic", null).Should().Be("sole-user-model");
        runner.ResolveDefaultModelWithSource("anthropic", null).Source
            .Should().Be("tenant-override");
    }

    // ── the skip-principal overload + the tenant routes' fallbackModel ──────
    // (bug 2026-07-27-tenant-surface-cannot-name-platform-default-under-override)

    [Test]
    public void SkipPrincipal_OverrideActive_AnswersThePlatformDbLeg()
    {
        var store = new FakeSettingsStore
        {
            TenantModels = { [("openai", Tenant)] = "tenant-model" },
            PlatformModels = { ["openai"] = "platform-model" },
        };
        var runner = Runner(Config(), store);

        // The normal resolution answers the override…
        runner.ResolveDefaultModelWithSource("openai", Tenant).Model.Should().Be("tenant-model");

        // …the skip-principal overload answers what a reset would land on.
        var fallback = runner.ResolveDefaultModelWithSource("openai", Tenant, skipPrincipal: true);
        fallback.Model.Should().Be("platform-model");
        fallback.Source.Should().Be("platform-db");
    }

    [Test]
    public void SkipPrincipal_FallsThroughConfigToTheDescriptorFloor()
    {
        var tenantOnly = new FakeSettingsStore
        {
            TenantModels = { [("openai", Tenant)] = "tenant-model" },
        };

        // No platform row → the config leg answers…
        var config = Config(new Dictionary<string, string?>
        {
            ["LlmProviders:openai:DefaultModel"] = "cfg-model",
        });
        var viaConfig = Runner(config, tenantOnly)
            .ResolveDefaultModelWithSource("openai", Tenant, skipPrincipal: true);
        viaConfig.Model.Should().Be("cfg-model");
        viaConfig.Source.Should().Be("config");

        // …no config either → the descriptor floor (openai ships a default).
        var viaDescriptor = Runner(Config(), tenantOnly)
            .ResolveDefaultModelWithSource("openai", Tenant, skipPrincipal: true);
        viaDescriptor.Model.Should().Be("gpt-4o");
        viaDescriptor.Source.Should().Be("descriptor");
    }

    [Test]
    public void SkipPrincipal_DescriptorFloorCanBeEmpty_GroqShipsNoDefault()
    {
        // The floor is NOT "never empty": several catalogue descriptors carry
        // DefaultModel "" ("caller must specify") — groq among them — so the
        // tenant routes' fallbackModel maps "" → null there. Pinned so the
        // customer UI's generic-confirm branch stays honest.
        var store = new FakeSettingsStore
        {
            TenantModels = { [("groq", Tenant)] = "tenant-model" },
        };
        var fallback = Runner(Config(), store)
            .ResolveDefaultModelWithSource("groq", Tenant, skipPrincipal: true);
        fallback.Model.Should().Be("");
        fallback.Source.Should().Be("descriptor");
    }

    [Test]
    public void SkipPrincipal_SingleUserMode_SkipsTheSoleUsersRowToo()
    {
        var store = new FakeSettingsStore
        {
            SingleUserMode = true,
            UserModels = { ["anthropic"] = "sole-user-model" },
            PlatformModels = { ["anthropic"] = "platform-model" },
        };
        var runner = Runner(Config(), store);

        // tenantId null does NOT skip the principal leg in single-user mode
        // (the store maps mode internally) — only the explicit flag does.
        runner.ResolveDefaultModelWithSource("anthropic", null).Model
            .Should().Be("sole-user-model");
        runner.ResolveDefaultModelWithSource("anthropic", null, skipPrincipal: true).Model
            .Should().Be("platform-model");
    }

    [Test]
    public void SkipPrincipalFalse_ByteIdenticalToTheTwoArgumentOverload()
    {
        var store = new FakeSettingsStore
        {
            TenantModels = { [("openai", Tenant)] = "tenant-model" },
            PlatformModels = { ["anthropic"] = "platform-model" },
        };
        var runner = Runner(Config(), store);

        foreach (var key in new[] { "openai", "anthropic", "groq", "not-a-provider" })
        {
            foreach (var tenantId in new Guid?[] { null, Tenant })
            {
                runner.ResolveDefaultModelWithSource(key, tenantId, skipPrincipal: false)
                    .Should().Be(runner.ResolveDefaultModelWithSource(key, tenantId),
                        $"key {key}, tenant {tenantId}");
            }
        }
    }

    // ── endpoint surface: GET …/{provider}/model carries fallbackModel ──────

    private sealed class StubTenantContext(Guid? id) : Tamma.Data.ITenantContext
    {
        public Guid? TenantId { get; private set; } = id;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private static Tamma.Api.Endpoints.TenantProviderModelResponse GetModelBody(
        string provider, FakeSettingsStore store, Guid? tenantId,
        IConfiguration? configuration = null)
    {
        var result = Tamma.Api.Endpoints.ProviderCredentialEndpoints.GetTenantProviderModel(
            provider, new StubTenantContext(tenantId), store,
            Runner(configuration ?? Config(), store));
        return result
            .Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults
                .Ok<Tamma.Api.Endpoints.TenantProviderModelResponse>>()
            .Subject.Value!;
    }

    [Test]
    public void GetTenantProviderModel_OverrideActive_NamesThePlatformFallback()
    {
        // THE bug case: while the override is active every resolved read IS
        // the override — fallbackModel now states what a reset lands on.
        var store = new FakeSettingsStore
        {
            TenantModels = { [("openai", Tenant)] = "tenant-model" },
            PlatformModels = { ["openai"] = "platform-model" },
        };

        var body = GetModelBody("openai", store, Tenant);

        body.Model.Should().Be("tenant-model");
        body.Source.Should().Be("tenant-override");
        body.Override.Should().Be("tenant-model");
        body.FallbackModel.Should().Be("platform-model");
    }

    [Test]
    public void GetTenantProviderModel_NoOverride_FallbackEqualsTheResolvedModel()
    {
        var store = new FakeSettingsStore { PlatformModels = { ["openai"] = "platform-model" } };

        var body = GetModelBody("openai", store, Tenant);

        body.Model.Should().Be("platform-model");
        body.Source.Should().Be("platform-db");
        body.Override.Should().BeNull();
        body.FallbackModel.Should().Be("platform-model");
    }

    [Test]
    public void GetTenantProviderModel_NothingBelowTheOverride_FallbackNull()
    {
        // groq: no platform row, no config, descriptor default "" → null.
        var store = new FakeSettingsStore
        {
            TenantModels = { [("groq", Tenant)] = "tenant-model" },
        };

        var body = GetModelBody("groq", store, Tenant);

        body.Model.Should().Be("tenant-model");
        body.FallbackModel.Should().BeNull(
            "nothing below the principal leg names a model for this provider");
    }

    [Test]
    public void GetTenantProviderModel_DescriptorFloor_FallbackIsTheDescriptorDefault()
    {
        var store = new FakeSettingsStore
        {
            TenantModels = { [("openai", Tenant)] = "tenant-model" },
        };

        GetModelBody("openai", store, Tenant).FallbackModel.Should().Be("gpt-4o");
    }

    // ── an explicitly-named per-call model never consults the resolver ─────

    [Test]
    public async Task RunAsync_WithExplicitModel_NeverConsultsTheSettingsStore()
    {
        // Chain entries / agent configs that NAME a model pass it straight to
        // RunAsync — the resolver must not be in that path (46-1 DoD: "a chain
        // entry naming a model still wins"). A store that throws on ANY read
        // proves the run never touches it.
        var handler = new ScriptedHandler("""
            {"choices":[{"finish_reason":"stop","message":{"content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1},"model":"explicit-model"}
            """);
        var runner = new InlineToolLoopRunner(
            logger: null,
            httpClientFactory: new SingleClientFactory(handler),
            configuration: Config(),
            sanitizer: null,
            settingsStore: new ThrowingSettingsStore());

        var result = await runner.RunAsync(
            provider: "openai",
            providerConfig: new Tamma.Activities.LlmCall.Models.LlmProviderConfig
            {
                Name = "openai",
                ApiKey = "k",
            },
            model: "explicit-model",
            systemPrompt: "s",
            userPrompt: "u",
            maxTokens: 16,
            temperature: 0.1,
            tools: null,
            enableToolLoop: true,
            loopConfig: new Tamma.Activities.LlmCall.Models.ToolLoopConfig { MaxSteps = 1 },
            correlationId: "named-model-wins",
            repair: null,
            ct: CancellationToken.None);

        result.Response.Success.Should().BeTrue();
        var body = JsonDocument.Parse(handler.LastBody!).RootElement;
        body.GetProperty("model").GetString().Should().Be("explicit-model");
    }

    // ── AC9 test 8: LlmProxyService pickup ─────────────────────────────────

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly string _json;
        public string? LastBody { get; private set; }
        public ScriptedHandler(string json) => _json = json;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public Uri? BaseAddress { get; init; }
        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(_handler, disposeHandler: false);
            if (BaseAddress is not null) client.BaseAddress = BaseAddress;
            return client;
        }
    }

    private static (LlmProxyService Proxy, ScriptedHandler Handler) ProxyWithStore(
        IProviderSettingsStore? store)
    {
        var handler = new ScriptedHandler("""
            {"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1,"output_tokens":2},"model":"m"}
            """);
        var tagger = new Mock<IBillingModeTagger>();
        tagger
            .Setup(t => t.ResolveTagAsync(
                It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("platform");
        var diagnostics = new Mock<IDiagnosticsService>();
        diagnostics
            .Setup(d => d.RecordEventAsync(
                It.IsAny<Tamma.Data.Entities.ProviderDiagnostic>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        diagnostics
            .Setup(d => d.GetBudgetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid accountId, CancellationToken _) =>
                new Tamma.Api.Services.Diagnostics.Models.BudgetStatus(
                    accountId, DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddMonths(1),
                    Spent: 0m, Limit: 1_000_000m, Remaining: 1_000_000m,
                    PercentUsed: 0, AlertThreshold: 0.8,
                    ShouldAlert: false, IsOverBudget: false));

        var proxy = new LlmProxyService(
            new SingleClientFactory(handler) { BaseAddress = new Uri("https://api.anthropic.com") },
            diagnostics.Object,
            tagger.Object,
            Mock.Of<Tamma.Data.Repositories.IEventRepository>(),
            NullLogger<LlmProxyService>.Instance,
            store);
        return (proxy, handler);
    }

    private static async Task<string?> SentModelAsync(
        LlmProxyService proxy, ScriptedHandler handler, string? requestModel, Guid? tenantId)
    {
        await proxy.ChatAsync(
            new ChatRequest(requestModel,
                new[] { new ChatMessage("user", "hi") }, MaxTokens: 16, Temperature: null),
            tenantId);
        return JsonDocument.Parse(handler.LastBody!).RootElement.GetProperty("model").GetString();
    }

    [Test]
    public async Task LlmProxy_RequestModel_WinsOverStore()
    {
        var store = new FakeSettingsStore
        {
            TenantModels = { [("anthropic", Tenant)] = "tenant-model" },
            PlatformModels = { ["anthropic"] = "platform-model" },
        };
        var (proxy, handler) = ProxyWithStore(store);

        (await SentModelAsync(proxy, handler, "explicit-model", Tenant))
            .Should().Be("explicit-model");
    }

    [Test]
    public async Task LlmProxy_NoRequestModel_TenantThenPlatformThenConst()
    {
        var store = new FakeSettingsStore
        {
            TenantModels = { [("anthropic", Tenant)] = "tenant-model" },
            PlatformModels = { ["anthropic"] = "platform-model" },
        };
        var (proxy, handler) = ProxyWithStore(store);
        (await SentModelAsync(proxy, handler, null, Tenant)).Should().Be("tenant-model");

        var platformOnly = new FakeSettingsStore
        {
            PlatformModels = { ["anthropic"] = "platform-model" },
        };
        (proxy, handler) = ProxyWithStore(platformOnly);
        (await SentModelAsync(proxy, handler, null, Tenant)).Should().Be("platform-model");
        (proxy, handler) = ProxyWithStore(platformOnly);
        (await SentModelAsync(proxy, handler, null, null)).Should().Be(
            "platform-model", "the platform leg applies with no tenant context too");
    }

    [Test]
    public async Task LlmProxy_NoStoreNoRequestModel_FallsToTheCorrectedConst()
    {
        var (proxy, handler) = ProxyWithStore(store: null);

        (await SentModelAsync(proxy, handler, null, null)).Should().Be(
            "claude-sonnet-4-5",
            "the AC7-corrected dash-formed API id — claude-sonnet-4.5 was a display name");
    }
}
