using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Security;

namespace Tamma.Api.Tests.Security;

/// <summary>
/// Story 32-4 — branch + side-effect tests for <see cref="SaaSProviderGate"/>.
/// Covers: single-user hard no-op (zero side effects, lookup never consulted);
/// the four SaaS outcomes (api-key+entitled allow, cli-token deny, unknown deny
/// fail-closed, not-entitled deny); the AGENT.PROVIDER.GATED event + metric
/// shape; event-append-failure swallow; and the "never throws on denial"
/// invariant.
/// </summary>
[TestFixture]
public class SaaSProviderGateTests
{
    private FakeAuthLookup _lookup = null!;
    private RecordingGateEventRepository _events = null!;
    private ProviderGatingMetrics _metrics = null!;

    [SetUp]
    public void SetUp()
    {
        _lookup = FakeAuthLookup.Default();
        _events = new RecordingGateEventRepository();
        _metrics = new ProviderGatingMetrics();
    }

    [TearDown]
    public void TearDown() => _metrics.Dispose();

    private SaaSProviderGate BuildGate(TammaMode mode, bool entitled = true) =>
        GateTestHelpers.Build(
            new StubMode(mode), _lookup, new FakeEntitlement(entitled), _events, _metrics);

    private static ProviderGateContext Ctx(string provider) =>
        new(provider, Role: "developer", Action: "implement", TenantId: Guid.NewGuid());

    // ── Single-user: hard no-op ─────────────────────────────────────────

    [TestCase("anthropic")]
    [TestCase("claude-code")]
    [TestCase("opencode")]
    [TestCase("definitely-unknown")]
    public async Task SingleUser_allows_every_provider_with_zero_side_effects(string provider)
    {
        var gate = BuildGate(TammaMode.SingleUser);

        var decision = await gate.InspectAsync(Ctx(provider));

        decision.Allowed.Should().BeTrue();
        decision.Outcome.Should().Be(ProviderGateOutcome.Allowed);
        decision.HttpStatusHint.Should().Be(200);
        decision.Reason.Should().BeNull();

        _lookup.Calls.Should().BeEmpty("single-user short-circuits before any lookup");
        _events.Appended.Should().BeEmpty("single-user emits no events");
        _metrics.GatedTotal.Should().Be(0, "single-user increments no metric");
    }

    // ── SaaS: api-key + entitled ⇒ allow ────────────────────────────────

    [TestCase("anthropic")]
    [TestCase("openai")]
    [TestCase("openrouter")]
    [TestCase("gemini")]
    public async Task SaaS_apiKey_entitled_allows_with_no_event_or_metric(string provider)
    {
        var gate = BuildGate(TammaMode.SaaS, entitled: true);

        var decision = await gate.InspectAsync(Ctx(provider));

        decision.Allowed.Should().BeTrue();
        decision.Outcome.Should().Be(ProviderGateOutcome.Allowed);
        decision.AuthModel.Should().Be(ProviderAuthModel.ApiKey);
        decision.HttpStatusHint.Should().Be(200);

        _events.Appended.Should().BeEmpty();
        _metrics.GatedTotal.Should().Be(0);
    }

    // ── SaaS: cli-token ⇒ deny 400 ──────────────────────────────────────

    [TestCase("claude-code")]
    [TestCase("opencode")]
    [TestCase("zen-mcp")]
    public async Task SaaS_cliToken_denies_400_with_one_event_and_one_metric(string provider)
    {
        var ctx = Ctx(provider);
        var gate = BuildGate(TammaMode.SaaS);

        var decision = await gate.InspectAsync(ctx);

        decision.Allowed.Should().BeFalse();
        decision.Outcome.Should().Be(ProviderGateOutcome.SaasProviderNotAllowed);
        decision.HttpStatusHint.Should().Be(400);
        decision.AuthModel.Should().Be(ProviderAuthModel.CliToken);
        decision.Reason.Should().NotBeNullOrWhiteSpace();
        // Key-free: references the provider name + the "api-key providers only"
        // policy text, but never secret key material (no `sk-` token etc.).
        decision.Reason!.ToLowerInvariant().Should().NotContain("sk-");
        decision.Reason.Should().Contain(provider);

        _events.Appended.Should().ContainSingle();
        var evt = _events.Appended[0];
        evt.Type.Should().Be("AGENT.PROVIDER.GATED");
        evt.TenantId.Should().Be(ctx.TenantId);

        using var data = JsonDocument.Parse(evt.Data);
        data.RootElement.GetProperty("provider").GetString().Should().Be(provider);
        data.RootElement.GetProperty("authModel").GetString().Should().Be("cli-token");
        data.RootElement.GetProperty("mode").GetString().Should().Be("saas");
        data.RootElement.GetProperty("reason").GetString().Should().Be("CLI_TOKEN_PROVIDER");
        data.RootElement.GetProperty("role").GetString().Should().Be("developer");
        data.RootElement.GetProperty("action").GetString().Should().Be("implement");

        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.GetProperty("tenantId").GetString().Should().Be(ctx.TenantId!.Value.ToString());
        tags.RootElement.GetProperty("provider").GetString().Should().Be(provider);
        tags.RootElement.GetProperty("authModel").GetString().Should().Be("cli-token");

        _metrics.GatedTotal.Should().Be(1);
    }

    // ── SaaS: unknown ⇒ deny 400 fail-closed ────────────────────────────

    [Test]
    public async Task SaaS_unknown_denies_400_failclosed_with_one_event_and_metric()
    {
        var ctx = Ctx("totally-unknown-provider");
        var gate = BuildGate(TammaMode.SaaS);

        var decision = await gate.InspectAsync(ctx);

        decision.Allowed.Should().BeFalse();
        decision.Outcome.Should().Be(ProviderGateOutcome.SaasProviderNotAllowed);
        decision.HttpStatusHint.Should().Be(400);
        decision.AuthModel.Should().BeNull("an unknown provider has no resolved auth model");

        _events.Appended.Should().ContainSingle();
        using var data = JsonDocument.Parse(_events.Appended[0].Data);
        data.RootElement.GetProperty("reason").GetString().Should().Be("PROVIDER_UNKNOWN");
        data.RootElement.GetProperty("authModel").GetString().Should().Be("unknown");

        _metrics.GatedTotal.Should().Be(1);
    }

    // ── SaaS: api-key + NOT entitled ⇒ deny 403 ─────────────────────────

    [Test]
    public async Task SaaS_apiKey_notEntitled_denies_403_with_one_event_and_metric()
    {
        var ctx = Ctx("anthropic");
        var gate = BuildGate(TammaMode.SaaS, entitled: false);

        var decision = await gate.InspectAsync(ctx);

        decision.Allowed.Should().BeFalse();
        decision.Outcome.Should().Be(ProviderGateOutcome.TenantNotEntitled);
        decision.HttpStatusHint.Should().Be(403);
        decision.AuthModel.Should().Be(ProviderAuthModel.ApiKey);

        _events.Appended.Should().ContainSingle();
        using var data = JsonDocument.Parse(_events.Appended[0].Data);
        data.RootElement.GetProperty("reason").GetString().Should().Be("TENANT_NOT_ENTITLED");
        data.RootElement.GetProperty("authModel").GetString().Should().Be("api-key");

        _metrics.GatedTotal.Should().Be(1);
    }

    [Test]
    public async Task SaaS_cliToken_does_not_consult_entitlement()
    {
        var entitlement = new FakeEntitlement(entitled: true);
        var gate = GateTestHelpers.Build(
            new StubMode(TammaMode.SaaS), _lookup, entitlement, _events, _metrics);

        await gate.InspectAsync(Ctx("claude-code"));

        entitlement.Calls.Should().BeEmpty(
            "cli-token is denied at the auth-model branch, before the entitlement check");
    }

    // ── Event-append failure is swallowed ───────────────────────────────

    [Test]
    public async Task SaaS_denial_swallows_event_append_failure_and_still_returns_decision()
    {
        var throwingEvents = new RecordingGateEventRepository(throwOnAppend: true);
        var gate = GateTestHelpers.Build(
            new StubMode(TammaMode.SaaS), _lookup, new FakeEntitlement(true), throwingEvents, _metrics);

        // Should NOT throw — the typed decision is still returned.
        var decision = await gate.InspectAsync(Ctx("claude-code"));

        decision.Allowed.Should().BeFalse();
        decision.Outcome.Should().Be(ProviderGateOutcome.SaasProviderNotAllowed);
        // Metric is incremented even though the event append failed.
        _metrics.GatedTotal.Should().Be(1);
    }

    // ── Never throws to signal a denial ─────────────────────────────────

    [TestCase(TammaMode.SaaS, "claude-code")]
    [TestCase(TammaMode.SaaS, "unknown-x")]
    [TestCase(TammaMode.SaaS, "anthropic")]
    [TestCase(TammaMode.SingleUser, "claude-code")]
    public async Task InspectAsync_never_throws_for_a_valid_context(TammaMode mode, string provider)
    {
        var gate = BuildGate(mode, entitled: true);
        var act = async () => await gate.InspectAsync(Ctx(provider));
        await act.Should().NotThrowAsync();
    }

    // ── Null context is the only throw (contract violation) ─────────────

    [Test]
    public async Task Null_context_throws_ArgumentNullException()
    {
        var gate = BuildGate(TammaMode.SaaS);
        var act = async () => await gate.InspectAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Case-insensitive / trimmed provider matching ────────────────────

    [Test]
    public async Task SaaS_classification_is_case_insensitive_and_trimmed()
    {
        var gate = BuildGate(TammaMode.SaaS);

        var decision = await gate.InspectAsync(Ctx("Claude-Code "));

        decision.Allowed.Should().BeFalse();
        decision.AuthModel.Should().Be(ProviderAuthModel.CliToken);
        decision.Outcome.Should().Be(ProviderGateOutcome.SaasProviderNotAllowed);
    }
}
