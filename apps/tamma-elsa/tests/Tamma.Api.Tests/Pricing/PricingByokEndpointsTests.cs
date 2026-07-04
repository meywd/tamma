using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Security;
using Tamma.Api.Tests.Security;
using Tamma.Data;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-3 — the BYOK toggle endpoints driven directly against the static handlers
/// with service/lookup fakes (mirrors <c>IntegrationCredentialEndpointsTests</c>).
/// Pins: enable → Ok + reveal-SAFE (no key in the response) + the service is called;
/// a cli-token / unknown provider in SaaS → 422 (single-user is a no-op); missing key /
/// no tenant handling; disable → Ok mode=platform; GET mode read. (Member-role 403 is
/// enforced by the PricingManage route policy and pinned by <c>PricingByokRbacTests</c>.)
/// </summary>
[TestFixture]
public class PricingByokEndpointsTests
{
    private const string Key = "sk-fake-byok-key-value";
    private static readonly Guid Tenant = Guid.NewGuid();

    private FakeBillingService _service = null!;

    [SetUp]
    public void SetUp() => _service = new FakeBillingService();

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) }, "test"));

    private static ITenantContext TenantCtx(Guid? id) => new StubTenant(id);
    private static ILoggerFactory Lf => NullLoggerFactory.Instance;

    private static object? Value(IResult result) =>
        result.GetType().GetProperty("Value")?.GetValue(result);

    // ── enable ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Enable_SaaS_ApiKeyProvider_Ok_RevealSafe_CallsService()
    {
        var result = await PricingEndpoints.EnableByok(
            "anthropic", new EnableByokRequest(Key), Principal(), TenantCtx(Tenant),
            new StubMode(TammaMode.SaaS), FakeAuthLookup.Default(), _service, Lf, CancellationToken.None);

        result.GetType().Name.Should().Contain("Ok");
        JsonSerializer.Serialize(Value(result)).Should().Contain("byok").And.NotContain(Key);
        _service.EnableCalls.Should().ContainSingle();
        _service.EnableCalls[0].Provider.Should().Be("anthropic");
        _service.EnableCalls[0].ApiKey.Should().Be(Key);
        _service.EnableCalls[0].Tenant.Should().Be(Tenant);
    }

    [Test]
    public async Task Enable_SaaS_CliTokenProvider_422_NotCalled()
    {
        var result = await PricingEndpoints.EnableByok(
            "claude-code", new EnableByokRequest(Key), Principal(), TenantCtx(Tenant),
            new StubMode(TammaMode.SaaS), FakeAuthLookup.Default(), _service, Lf, CancellationToken.None);

        result.GetType().Name.Should().Contain("UnprocessableEntity");
        JsonSerializer.Serialize(Value(result)).Should().Contain("CLI providers are single-user only");
        _service.EnableCalls.Should().BeEmpty("a cli-token provider is not BYOK-eligible in SaaS");
    }

    [Test]
    public async Task Enable_SaaS_UnknownProvider_422_FailClosed()
    {
        var result = await PricingEndpoints.EnableByok(
            "totally-unknown", new EnableByokRequest(Key), Principal(), TenantCtx(Tenant),
            new StubMode(TammaMode.SaaS), FakeAuthLookup.Default(), _service, Lf, CancellationToken.None);

        result.GetType().Name.Should().Contain("UnprocessableEntity");
        _service.EnableCalls.Should().BeEmpty("an unknown provider fails closed in SaaS");
    }

    [Test]
    public async Task Enable_SingleUser_CliTokenProvider_Allowed_NoLookup()
    {
        var lookup = FakeAuthLookup.Default();
        var result = await PricingEndpoints.EnableByok(
            "claude-code", new EnableByokRequest(Key), Principal(), TenantCtx(Tenant),
            new StubMode(TammaMode.SingleUser), lookup, _service, Lf, CancellationToken.None);

        // "CLI providers are single-user only" — the sole user may BYOK a CLI provider.
        result.GetType().Name.Should().Contain("Ok");
        _service.EnableCalls.Should().ContainSingle();
        lookup.Calls.Should().BeEmpty("the SaaS eligibility gate is a hard no-op in single-user");
    }

    [Test]
    public async Task Enable_MissingKey_BadRequest_NotCalled()
    {
        var result = await PricingEndpoints.EnableByok(
            "anthropic", new EnableByokRequest("  "), Principal(), TenantCtx(Tenant),
            new StubMode(TammaMode.SaaS), FakeAuthLookup.Default(), _service, Lf, CancellationToken.None);

        result.GetType().Name.Should().Contain("BadRequest");
        _service.EnableCalls.Should().BeEmpty();
    }

    [Test]
    public async Task Enable_NoTenantContext_BadRequest_NotCalled()
    {
        var result = await PricingEndpoints.EnableByok(
            "anthropic", new EnableByokRequest(Key), Principal(), TenantCtx(null),
            new StubMode(TammaMode.SaaS), FakeAuthLookup.Default(), _service, Lf, CancellationToken.None);

        result.GetType().Name.Should().Contain("BadRequest");
        _service.EnableCalls.Should().BeEmpty();
    }

    // ── disable ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Disable_Ok_Platform_CallsService()
    {
        var result = await PricingEndpoints.DisableByok(
            "anthropic", Principal(), TenantCtx(Tenant), _service, Lf, CancellationToken.None);

        result.GetType().Name.Should().Contain("Ok");
        JsonSerializer.Serialize(Value(result)).Should().Contain("platform");
        _service.DisableCalls.Should().ContainSingle().Which.Provider.Should().Be("anthropic");
    }

    [Test]
    public async Task Disable_NoTenantContext_NotFound()
    {
        var result = await PricingEndpoints.DisableByok(
            "anthropic", Principal(), TenantCtx(null), _service, Lf, CancellationToken.None);

        result.GetType().Name.Should().Contain("NotFound");
        _service.DisableCalls.Should().BeEmpty();
    }

    // ── read ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetMode_Ok_RevealSafe()
    {
        _service.GetResult = new ByokModeResult("anthropic", "byok", KeySet: true);
        var result = await PricingEndpoints.GetProviderMode(
            "anthropic", TenantCtx(Tenant), _service, CancellationToken.None);

        result.GetType().Name.Should().Contain("Ok");
        var json = JsonSerializer.Serialize(Value(result));
        json.Should().Contain("keySet").And.Contain("byok").And.NotContain(Key);
    }

    [Test]
    public async Task List_NoTenant_EmptyOk()
    {
        var result = await PricingEndpoints.ListProviderModes(
            TenantCtx(null), _service, CancellationToken.None);
        result.GetType().Name.Should().Contain("Ok");
    }

    // ── fakes ─────────────────────────────────────────────────────────────────

    private sealed class StubTenant(Guid? id) : ITenantContext
    {
        public Guid? TenantId { get; private set; } = id;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private sealed class FakeBillingService : ITenantProviderBillingService
    {
        public List<(Guid Tenant, string Provider, string ApiKey)> EnableCalls { get; } = new();
        public List<(Guid Tenant, string Provider)> DisableCalls { get; } = new();
        public ByokModeResult? GetResult { get; set; }

        public Task<ByokModeResult> EnableByokAsync(
            Guid tenantId, string provider, string apiKey, Guid? actorUserId, CancellationToken ct = default)
        {
            EnableCalls.Add((tenantId, provider, apiKey));
            return Task.FromResult(new ByokModeResult(provider, "byok", KeySet: true));
        }

        public Task<ByokModeResult> DisableByokAsync(
            Guid tenantId, string provider, Guid? actorUserId, CancellationToken ct = default)
        {
            DisableCalls.Add((tenantId, provider));
            return Task.FromResult(new ByokModeResult(provider, "platform", KeySet: false));
        }

        public Task<ByokModeResult> GetModeAsync(Guid tenantId, string provider, CancellationToken ct = default) =>
            Task.FromResult(GetResult ?? new ByokModeResult(provider, "platform", KeySet: false));

        public Task<IReadOnlyList<ByokModeResult>> ListModesAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ByokModeResult>>(Array.Empty<ByokModeResult>());
    }
}
