using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Api.Auth;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Audit;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Providers;
using Tamma.Core;
using Tamma.Core.Audit;

namespace Tamma.Activities.Tests.Providers;

/// <summary>
/// Epic 46 review follow-ups — direct-invocation coverage of the settings
/// MUTATION endpoints (F6): platform PUT (set / enabled / disabled), platform
/// DELETE, tenant PUT, tenant DELETE — each pinning the
/// <c>PROVIDER.SETTINGS_CHANGED.SUCCESS</c> emission (operation/scope/mode
/// tags, previous→new model in data, no key material). Plus the F4
/// platform-surface pins (audit previousModel and the admin roster /
/// admin models "current" pin report PLATFORM-layer values even when the
/// single-user sole user holds an override), the F9 <c>mode</c> tag, and the
/// F11 disabled-provider 404s on the tenant read routes.
///
/// <para><b>RBAC note (F6/F3, policy wiring is declarative):</b> the member
/// 403 on mutations and the member-reach reads are enforced by route-map
/// policies in <c>Program.cs</c> (mutations: the /api/v1/agents group's
/// SettingsView + AgentManage; reads: AuthenticatedAny outside the group) —
/// not unit-invokable here. What IS unit-assertable is the permission matrix
/// those policies resolve through — pinned in
/// <see cref="PermissionMatrix_PinsWhyReadsMovedOffTheGroupGate"/>.</para>
/// </summary>
[TestFixture]
public class ProviderSettingsEndpointsTests
{
    private static readonly Guid Tenant = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
    private static readonly Guid Actor = Guid.Parse("11111111-2222-3333-4444-555555555555");

    // ── fakes ───────────────────────────────────────────────────────────────

    /// <summary>Writable settings-store fake (the read-only
    /// <c>FakeSettingsStore</c> in ProviderSettingsResolutionTests throws on
    /// writes; the mutation endpoints need working ones).</summary>
    private sealed class WritableSettingsStore : IProviderSettingsStore
    {
        public Dictionary<(string Provider, Guid Tenant), string> TenantModels { get; } = new();
        public Dictionary<string, string> UserModels { get; } =
            new(StringComparer.OrdinalIgnoreCase);
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

        public Task SetPlatformModelAsync(
            string p, string m, Guid? by, CancellationToken ct = default)
        {
            PlatformModels[Canonical(p)] = m;
            return Task.CompletedTask;
        }

        public Task SetEnabledAsync(string p, bool e, Guid? by, CancellationToken ct = default)
        {
            EnabledFlags[Canonical(p)] = e;
            return Task.CompletedTask;
        }

        public Task<bool> RemovePlatformAsync(string p, CancellationToken ct = default)
        {
            var key = Canonical(p);
            var existed = PlatformModels.Remove(key);
            existed |= EnabledFlags.Remove(key);
            return Task.FromResult(existed);
        }

        public Task SetPrincipalModelAsync(
            string p, Guid? tenantId, Guid? userId, string m, Guid? by,
            CancellationToken ct = default)
        {
            if (tenantId is Guid tid) TenantModels[(Canonical(p), tid)] = m;
            else UserModels[Canonical(p)] = m;
            return Task.CompletedTask;
        }

        public Task<bool> RemovePrincipalModelAsync(
            string p, Guid? tenantId, Guid? userId, CancellationToken ct = default)
        {
            var removed = tenantId is Guid tid
                ? TenantModels.Remove((Canonical(p), tid))
                : UserModels.Remove(Canonical(p));
            return Task.FromResult(removed);
        }

        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;

        private static string Canonical(string key) => ProviderCatalog.Resolve(key)?.Key ?? key;
    }

    private sealed class RecordingEmitter : ISensitiveActionEmitter
    {
        public List<SensitiveAction> Emitted { get; } = new();
        public Task EmitAsync(SensitiveAction action, CancellationToken ct = default)
        {
            Emitted.Add(action);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedMode(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

    private sealed class FakePricing : IProviderPricingService
    {
        public bool Known { get; init; } = true;
        public decimal Compute(string p, string? m, int i, int o) => 0m;
        public bool IsKnown(string p, string? m) => Known;
    }

    private sealed class StubTenantContext(Guid? id) : Tamma.Data.ITenantContext
    {
        public Guid? TenantId { get; private set; } = id;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private sealed class FakeCatalog : IProviderModelCatalog
    {
        public ProviderModelList List { get; set; } = new(
            Array.Empty<ProviderModelInfo>(), FetchedAt: null, Stale: false, ErrorCode: null);
        public List<(string Provider, Guid? TenantId)> Invalidated { get; } = new();

        public Task<ProviderModelList> ListModelsAsync(
            string providerKey, Guid? tenantId, CancellationToken ct = default) =>
            Task.FromResult(List);

        public void Invalidate(string providerKey, Guid? tenantId) =>
            Invalidated.Add((providerKey, tenantId));
    }

    private sealed class ThrowingResolver : IProviderCredentialResolver
    {
        public Task<ProviderCredential> ResolveAsync(
            Guid? tenantId, string providerName, CancellationToken ct = default) =>
            throw new TammaError(
                "PROVIDER_CREDENTIAL_UNAVAILABLE", "no key",
                retryable: false, severity: TammaErrorSeverity.High);
        public void Invalidate(Guid? tenantId, string providerName) { }
    }

    private sealed class PlainHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static InlineToolLoopRunner Runner(IProviderSettingsStore store) =>
        new(logger: null, httpClientFactory: null, configuration: Config(),
            sanitizer: null,
            autonomyGate: new Tamma.Api.Services.Agents.CatalogDefaultToolLoopAutonomyGate(),
            settingsStore: store);

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Actor.ToString()),
        }, "test"));

    private static HttpContext Http(RecordingEmitter? emitter = null, TammaMode? mode = null)
    {
        var context = new DefaultHttpContext();
        var services = new ServiceCollection();
        if (emitter is not null) services.AddSingleton<ISensitiveActionEmitter>(emitter);
        if (mode is TammaMode m) services.AddSingleton<ITammaModeProvider>(new FixedMode(m));
        context.RequestServices = services.BuildServiceProvider();
        return context;
    }

    /// <summary>F6 — event hygiene: no tag/data key or value is key-shaped.</summary>
    private static void AssertNoKeyMaterial(SensitiveAction action)
    {
        var forbidden = new[] { "apikey", "api_key", "plaintext", "secret", "credential", "token" };
        var texts = new List<string>();
        texts.AddRange(action.Tags.Keys);
        texts.AddRange(action.Tags.Values.Where(v => v is not null).Select(v => v!));
        texts.AddRange(action.Data.Keys);
        texts.AddRange(action.Data.Values.Select(v => v?.ToString() ?? ""));
        foreach (var text in texts)
        {
            forbidden.Should().NotContain(
                f => text.ToLowerInvariant().Contains(f),
                $"'{text}' in the settings-changed event must not be key-shaped");
        }
    }

    // ── F6: platform PUT ────────────────────────────────────────────────────

    [Test]
    public async Task PlatformPut_SetModel_PersistsAndEmitsSetEvent()
    {
        var store = new WritableSettingsStore();
        var emitter = new RecordingEmitter();

        var result = await ProviderAdminEndpoints.PutProviderSettings(
            "openai", new PutProviderSettingsRequest("gpt-4o-mini", null),
            Principal(), store, Runner(store), new FakePricing(),
            Http(emitter, TammaMode.SaaS));

        var body = result
            .Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults
                .Ok<PutProviderSettingsResponse>>().Subject.Value!;
        body.Provider.Should().Be("openai");
        body.DefaultModel.Should().Be("gpt-4o-mini");
        body.Enabled.Should().BeTrue();
        body.PricingKnown.Should().BeTrue();
        store.PlatformModels["openai"].Should().Be("gpt-4o-mini");

        var action = emitter.Emitted.Should().ContainSingle().Subject;
        action.Type.Should().Be(SensitiveActionCatalog.ProviderSettingsChanged);
        action.Scope.Should().Be(SensitiveActionScope.Platform,
            "a platform mutation has no tenant principal");
        action.ActorUserId.Should().Be(Actor);
        action.Tags["provider"].Should().Be("openai");
        action.Tags["scope"].Should().Be("platform");
        action.Tags["operation"].Should().Be("set");
        action.Tags["mode"].Should().Be("saas", "F9 — the documented mode tag is emitted");
        action.Data["previousModel"].Should().Be("gpt-4o",
            "previous→new: nothing was persisted, so previous is the descriptor default");
        action.Data["model"].Should().Be("gpt-4o-mini");
        action.Data.Should().NotContainKey("enabled", "no flag change in this request");
        AssertNoKeyMaterial(action);
    }

    [TestCase(false, "disabled")]
    [TestCase(true, "enabled")]
    public async Task PlatformPut_EnabledFlagOnly_EmitsEnabledOrDisabledOperation(
        bool enabled, string expectedOperation)
    {
        var store = new WritableSettingsStore();
        var emitter = new RecordingEmitter();

        var result = await ProviderAdminEndpoints.PutProviderSettings(
            "groq", new PutProviderSettingsRequest(null, enabled),
            Principal(), store, Runner(store), new FakePricing(),
            Http(emitter, TammaMode.SaaS));

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults
            .Ok<PutProviderSettingsResponse>>();
        store.IsEnabled("groq").Should().Be(enabled);

        var action = emitter.Emitted.Should().ContainSingle().Subject;
        action.Tags["operation"].Should().Be(expectedOperation);
        action.Tags["scope"].Should().Be("platform");
        action.Data["enabled"].Should().Be(enabled);
        action.Data.Should().NotContainKey("model", "no model change in this request");
        AssertNoKeyMaterial(action);
    }

    // ── F6: platform DELETE ─────────────────────────────────────────────────

    [Test]
    public async Task PlatformDelete_RemovesRow_EmitsRemovedWithPreviousModel()
    {
        var store = new WritableSettingsStore
        {
            PlatformModels = { ["openai"] = "platform-model" },
        };
        var emitter = new RecordingEmitter();

        var result = await ProviderAdminEndpoints.DeleteProviderSettings(
            "openai", Principal(), store, Runner(store), Http(emitter, TammaMode.SaaS));

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NoContent>();
        store.PlatformModels.Should().NotContainKey("openai");

        var action = emitter.Emitted.Should().ContainSingle().Subject;
        action.Tags["operation"].Should().Be("removed");
        action.Tags["scope"].Should().Be("platform");
        action.Tags["mode"].Should().Be("saas");
        action.Data["previousModel"].Should().Be("platform-model");
        action.Data.Should().NotContainKey("model", "a removal has no new model");
        AssertNoKeyMaterial(action);
    }

    [Test]
    public async Task PlatformDelete_NoRow_404_NoEvent()
    {
        var store = new WritableSettingsStore();
        var emitter = new RecordingEmitter();

        var result = await ProviderAdminEndpoints.DeleteProviderSettings(
            "openai", Principal(), store, Runner(store), Http(emitter, TammaMode.SaaS));

        result.GetType().Name.Should().Contain("NotFound");
        emitter.Emitted.Should().BeEmpty("no mutation happened — nothing to audit");
    }

    // ── F4: single-user platform surface reports platform-layer values ──────

    [Test]
    public async Task SingleUser_PlatformPut_AuditsThePlatformValueAsPrevious_NotTheUsersOverride()
    {
        var store = new WritableSettingsStore
        {
            SingleUserMode = true,
            UserModels = { ["openai"] = "sole-users-override" },
            PlatformModels = { ["openai"] = "platform-model" },
        };
        var emitter = new RecordingEmitter();

        await ProviderAdminEndpoints.PutProviderSettings(
            "openai", new PutProviderSettingsRequest("new-platform-model", null),
            Principal(), store, Runner(store), new FakePricing(),
            Http(emitter, TammaMode.SingleUser));

        var action = emitter.Emitted.Should().ContainSingle().Subject;
        action.Data["previousModel"].Should().Be("platform-model",
            "F4 — a PLATFORM mutation's audit must record the platform-layer previous value; "
            + "the principal-inclusive resolve would have recorded the sole user's override");
        action.Data["model"].Should().Be("new-platform-model");
        action.Tags["mode"].Should().Be("single-user");
    }

    [Test]
    public async Task SingleUser_PlatformDelete_AuditsThePlatformValueAsPrevious()
    {
        var store = new WritableSettingsStore
        {
            SingleUserMode = true,
            UserModels = { ["openai"] = "sole-users-override" },
            PlatformModels = { ["openai"] = "platform-model" },
        };
        var emitter = new RecordingEmitter();

        await ProviderAdminEndpoints.DeleteProviderSettings(
            "openai", Principal(), store, Runner(store), Http(emitter, TammaMode.SingleUser));

        emitter.Emitted.Should().ContainSingle()
            .Which.Data["previousModel"].Should().Be("platform-model", "F4 (see the PUT test)");
    }

    [Test]
    public async Task SingleUser_AdminRoster_ShowsThePlatformValueAndSource()
    {
        var store = new WritableSettingsStore
        {
            SingleUserMode = true,
            UserModels = { ["openai"] = "sole-users-override" },
            PlatformModels = { ["openai"] = "platform-model" },
        };

        var result = await ProviderAdminEndpoints.ListProviderStatus(
            new PlainHttpClientFactory(), new ThrowingResolver(), Runner(store), store,
            Http());

        var value = result.GetType().GetProperty("Value")!.GetValue(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        var openai = doc.RootElement.GetProperty("providers").EnumerateArray()
            .Single(p => p.GetProperty("Key").GetString() == "openai");

        openai.GetProperty("CurrentModel").GetString().Should().Be("platform-model",
            "F4 — the ADMIN roster is the platform surface; in single-user mode the "
            + "principal-inclusive resolve would have shown the sole user's override");
        openai.GetProperty("Source").GetString().Should().Be("platform-db",
            "and its provenance must be the platform layer, not tenant-override");
    }

    [Test]
    public async Task SingleUser_AdminModelsRoute_CurrentPin_IsThePlatformModel()
    {
        var store = new WritableSettingsStore
        {
            SingleUserMode = true,
            UserModels = { ["openai"] = "sole-users-override" },
            PlatformModels = { ["openai"] = "platform-model" },
        };
        var catalog = new FakeCatalog
        {
            List = new ProviderModelList(
                new[]
                {
                    new ProviderModelInfo("platform-model", null, false),
                    new ProviderModelInfo("sole-users-override", null, false),
                },
                DateTimeOffset.UtcNow, Stale: false, ErrorCode: null),
        };

        var result = await ProviderAdminEndpoints.GetProviderModels(
            "openai", catalog, Runner(store), Http());

        var body = result
            .Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults
                .Ok<ProviderModelsResponse>>().Subject.Value!;
        body.Models.Single(m => m.Current).Id.Should().Be("platform-model",
            "F4 — the admin models list pins the PLATFORM-layer current model");
    }

    // ── F6: tenant PUT / DELETE ─────────────────────────────────────────────

    [Test]
    public async Task TenantPut_Set_PersistsOverride_EmitsTenantScopedEvent()
    {
        var store = new WritableSettingsStore
        {
            PlatformModels = { ["openai"] = "platform-model" },
        };
        var emitter = new RecordingEmitter();

        var result = await ProviderCredentialEndpoints.PutTenantProviderModel(
            "openai", new PutTenantProviderModelRequest("tenant-model"),
            Principal(), new StubTenantContext(Tenant), new FixedMode(TammaMode.SaaS),
            store, Runner(store), new FakePricing(), Http(emitter, TammaMode.SaaS));

        var body = result
            .Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults
                .Ok<PutTenantProviderModelResponse>>().Subject.Value!;
        body.Provider.Should().Be("openai");
        body.Model.Should().Be("tenant-model");
        store.TenantModels[("openai", Tenant)].Should().Be("tenant-model");

        var action = emitter.Emitted.Should().ContainSingle().Subject;
        action.Type.Should().Be(SensitiveActionCatalog.ProviderSettingsChanged);
        action.Scope.Should().Be(SensitiveActionScope.Tenant);
        action.TenantId.Should().Be(Tenant);
        action.Tags["scope"].Should().Be("tenant");
        action.Tags["operation"].Should().Be("set");
        action.Tags["mode"].Should().Be("saas");
        action.Data["previousModel"].Should().Be("platform-model",
            "previous→new: before the override, the platform row resolved");
        action.Data["model"].Should().Be("tenant-model");
        AssertNoKeyMaterial(action);
    }

    [Test]
    public async Task TenantPut_PlatformDisabledProvider_409_NoWriteNoEvent()
    {
        var store = new WritableSettingsStore
        {
            EnabledFlags = { ["openai"] = false },
        };
        var emitter = new RecordingEmitter();

        var result = await ProviderCredentialEndpoints.PutTenantProviderModel(
            "openai", new PutTenantProviderModelRequest("tenant-model"),
            Principal(), new StubTenantContext(Tenant), new FixedMode(TammaMode.SaaS),
            store, Runner(store), new FakePricing(), Http(emitter, TammaMode.SaaS));

        result.GetType().Name.Should().Contain("Conflict", "the platform off switch wins");
        store.TenantModels.Should().BeEmpty("no override may be written for a disabled provider");
        emitter.Emitted.Should().BeEmpty();
    }

    [Test]
    public async Task TenantDelete_RemovesOverride_EmitsRemovedWithTheOverrideAsPrevious()
    {
        var store = new WritableSettingsStore
        {
            TenantModels = { [("openai", Tenant)] = "tenant-model" },
            PlatformModels = { ["openai"] = "platform-model" },
        };
        var emitter = new RecordingEmitter();

        var result = await ProviderCredentialEndpoints.DeleteTenantProviderModel(
            "openai", Principal(), new StubTenantContext(Tenant), new FixedMode(TammaMode.SaaS),
            store, Runner(store), Http(emitter, TammaMode.SaaS));

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NoContent>();
        store.TenantModels.Should().BeEmpty();

        var action = emitter.Emitted.Should().ContainSingle().Subject;
        action.Scope.Should().Be(SensitiveActionScope.Tenant);
        action.TenantId.Should().Be(Tenant);
        action.Tags["operation"].Should().Be("removed");
        action.Tags["scope"].Should().Be("tenant");
        action.Data["previousModel"].Should().Be("tenant-model",
            "the removed override IS the previous effective model");
        AssertNoKeyMaterial(action);
    }

    [Test]
    public async Task TenantDelete_NoOverride_404_NoEvent()
    {
        var store = new WritableSettingsStore();
        var emitter = new RecordingEmitter();

        var result = await ProviderCredentialEndpoints.DeleteTenantProviderModel(
            "openai", Principal(), new StubTenantContext(Tenant), new FixedMode(TammaMode.SaaS),
            store, Runner(store), Http(emitter, TammaMode.SaaS));

        result.GetType().Name.Should().Contain("NotFound");
        emitter.Emitted.Should().BeEmpty();
    }

    [Test]
    public async Task SingleUser_TenantPut_WritesTheUserRow_EmitsUserScopeAndMode()
    {
        var store = new WritableSettingsStore { SingleUserMode = true };
        var emitter = new RecordingEmitter();

        var result = await ProviderCredentialEndpoints.PutTenantProviderModel(
            "anthropic", new PutTenantProviderModelRequest("sole-user-pick"),
            Principal(), new StubTenantContext(null), new FixedMode(TammaMode.SingleUser),
            store, Runner(store), new FakePricing(), Http(emitter, TammaMode.SingleUser));

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults
            .Ok<PutTenantProviderModelResponse>>();
        store.UserModels["anthropic"].Should().Be("sole-user-pick",
            "single-user overrides are USER-keyed (plan D3)");

        var action = emitter.Emitted.Should().ContainSingle().Subject;
        action.Tags["scope"].Should().Be("user");
        action.Tags["mode"].Should().Be("single-user", "F9");
        action.Data["model"].Should().Be("sole-user-pick");
        AssertNoKeyMaterial(action);
    }

    // ── F11: disabled provider 404s on the tenant read routes ───────────────

    private static string NotFoundBodyJson(IResult result)
    {
        result.GetType().Name.Should().Contain("NotFound");
        var value = result.GetType().GetProperty("Value")!.GetValue(result);
        return JsonSerializer.Serialize(value);
    }

    [Test]
    public async Task TenantModelsGet_PlatformDisabledProvider_404IdenticalToUnknownProvider()
    {
        var store = new WritableSettingsStore { EnabledFlags = { ["openai"] = false } };
        var catalog = new FakeCatalog();

        var disabled = await ProviderCredentialEndpoints.GetTenantProviderModels(
            "openai", new StubTenantContext(Tenant), new FixedMode(TammaMode.SaaS),
            store, catalog, Runner(store), Http());
        var unknown = await ProviderCredentialEndpoints.GetTenantProviderModels(
            "definitely-not-a-provider", new StubTenantContext(Tenant),
            new FixedMode(TammaMode.SaaS), store, catalog, Runner(store), Http());

        NotFoundBodyJson(disabled).Should().Be(NotFoundBodyJson(unknown),
            "F11 — a disabled provider must be indistinguishable from an unknown one "
            + "(never-enumerate posture; matches the roster's absence and the PUT's 409)");
    }

    [Test]
    public void TenantModelGet_PlatformDisabledProvider_404IdenticalToUnknownProvider()
    {
        var store = new WritableSettingsStore { EnabledFlags = { ["openai"] = false } };

        var disabled = ProviderCredentialEndpoints.GetTenantProviderModel(
            "openai", new StubTenantContext(Tenant), store, Runner(store));
        var unknown = ProviderCredentialEndpoints.GetTenantProviderModel(
            "definitely-not-a-provider", new StubTenantContext(Tenant), store, Runner(store));

        NotFoundBodyJson(disabled).Should().Be(NotFoundBodyJson(unknown), "F11 (see above)");
    }

    [Test]
    public void TenantModelGet_EnabledProvider_StillAnswers()
    {
        var store = new WritableSettingsStore
        {
            PlatformModels = { ["openai"] = "platform-model" },
        };

        var result = ProviderCredentialEndpoints.GetTenantProviderModel(
            "openai", new StubTenantContext(Tenant), store, Runner(store));

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults
                .Ok<TenantProviderModelResponse>>()
            .Which.Value!.Model.Should().Be("platform-model");
    }

    // ── F3/F6: what is unit-assertable about the route policies ─────────────

    [Test]
    public void PermissionMatrix_PinsWhyReadsMovedOffTheGroupGate()
    {
        // The /api/v1/agents group's SettingsView policy resolves through
        // settings:view — admin/owner only. That is exactly why the three
        // Epic 46 tenant GETs are mapped OUTSIDE the group on AuthenticatedAny
        // (F3): inheriting the group gate would 403 every SaaS member on reads
        // the story promises them. The mutations' member-403 comes from
        // agents:manage (admin/owner) via the AgentManage endpoint policy.
        // The route wiring itself is declarative in Program.cs and not
        // unit-invokable here — these matrix pins are the unit-assertable part.
        Permissions.HasPermission("member", "settings:view").Should().BeFalse(
            "settings:view is admin/owner — the group gate members must not inherit on reads");
        Permissions.HasPermission("admin", "settings:view").Should().BeTrue();
        Permissions.HasPermission("member", "agents:manage").Should().BeFalse(
            "mutations stay member-403 through AgentManage");
        Permissions.HasPermission("admin", "agents:manage").Should().BeTrue();
        Permissions.HasPermission("owner", "agents:manage").Should().BeTrue();
    }
}
