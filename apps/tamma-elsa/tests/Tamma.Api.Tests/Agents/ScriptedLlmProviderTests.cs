using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.Security;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Agents.Scripted;
using Tamma.Core.Documents;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// 2026-08-13 (Epic 31 P5 follow-up) — the opt-in "scripted" LLM provider:
/// deterministic response selection, the unscripted-cell typed error, the
/// script-override file, the structural production guard on registration, the
/// credential decorator, and the runner-level dispatch (no HTTP socket ever
/// opens for a scripted call).
/// </summary>
[TestFixture]
public class ScriptedLlmProviderTests
{
    // =====================================================================
    // Responder — selection + determinism
    // =====================================================================

    [Test]
    public void CanHandle_OnlyTheScriptedKey()
    {
        var responder = new ScriptedLlmResponder();
        responder.CanHandle("scripted").Should().BeTrue();
        responder.CanHandle("SCRIPTED").Should().BeTrue();
        responder.CanHandle(" scripted ").Should().BeTrue();
        responder.CanHandle("anthropic").Should().BeFalse();
        responder.CanHandle("").Should().BeFalse();
        responder.CanHandle(null).Should().BeFalse();
    }

    [Test]
    public void Respond_IsDeterministic_SameCallSameBytes()
    {
        var responder = new ScriptedLlmResponder();
        var call = new ScriptedLlmCall(
            "scripted", "architect", "plan-system-design", "plan", "any-model", "corr-1");

        var first = responder.Respond(call);
        var second = responder.Respond(call);

        first.Success.Should().BeTrue();
        second.ResponseText.Should().Be(first.ResponseText);
        second.PromptTokens.Should().Be(first.PromptTokens).And.Be(0,
            "the scripted provider spends nothing — budget/cost accounting stays truthful");
        second.CompletionTokens.Should().Be(0);
        first.StopReason.Should().Be(StopReason.EndTurn, "a scripted reply never asks for tools");
        first.ToolCalls.Should().BeNull();
    }

    [Test]
    public void Respond_DocumentTypedCall_FallsBackToTheRegistryValidExample()
    {
        var responder = new ScriptedLlmResponder();
        var call = new ScriptedLlmCall(
            "scripted", "architect", "plan-system-design", "plan", "m", "corr");

        var response = responder.Respond(call);

        response.Success.Should().BeTrue();
        // The payload must PASS the real registered validator — the registry's
        // own first VALID example guarantees it by the drift suite's self-check.
        var validation = DocumentTypeRegistry.Resolve("plan")
            .Validate(System.Text.Json.JsonDocument.Parse(response.ResponseText!).RootElement);
        validation.IsValid.Should().BeTrue(
            "the scripted produce response must satisfy the 39-9 validation ring: {0}",
            string.Join("; ", validation.Violations.Select(v => v.Code)));
    }

    [Test]
    public void Respond_FreeTextCell_UsesTheBuiltInCycleLibrary()
    {
        var responder = new ScriptedLlmResponder();
        var response = responder.Respond(new ScriptedLlmCall(
            "scripted", "product_owner", "summarize-stakeholder", null, "m", "corr"));

        response.Success.Should().BeTrue();
        response.ResponseText.Should().Be(ScriptedCycleLibrary.PoSummary);
    }

    [Test]
    public void Respond_UnscriptedCell_ReturnsTypedErrorNamingTheMissingKeys()
    {
        var responder = new ScriptedLlmResponder();
        var response = responder.Respond(new ScriptedLlmCall(
            "scripted", "tech_writer", "write-runbook", null, "m", "corr"));

        response.Success.Should().BeFalse();
        response.HttpStatusCode.Should().Be(422, "an unscripted cell is non-retryable, never transient");
        response.ErrorMessage.Should().Contain(ScriptedLlmResponder.MissingCellError)
            .And.Contain("tech_writer/write-runbook")
            .And.Contain("ScriptPath", "the error must tell the test author where to add the cell");
    }

    [Test]
    public void Respond_OverrideFile_WinsOverTheBuiltInLibrary_PerKey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scripted-{Guid.NewGuid():N}.json");
        File.WriteAllText(path,
            """{"responses":{"product_owner/summarize-stakeholder":"OVERRIDDEN","developer/custom-cell":"NEW"}}""");
        try
        {
            var overrides = ScriptedLlmResponder.LoadOverrides(path);
            var responder = new ScriptedLlmResponder(overrides);

            responder.Respond(new ScriptedLlmCall(
                    "scripted", "product_owner", "summarize-stakeholder", null, "m", "c"))
                .ResponseText.Should().Be("OVERRIDDEN");
            responder.Respond(new ScriptedLlmCall(
                    "scripted", "developer", "custom-cell", null, "m", "c"))
                .ResponseText.Should().Be("NEW");
            // Untouched built-in keys keep serving.
            responder.Respond(new ScriptedLlmCall(
                    "scripted", "devops", "deploy", null, "m", "c"))
                .ResponseText.Should().Be(ScriptedCycleLibrary.StageSuccess);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void LoadOverrides_MissingFile_FailsLoud()
    {
        var act = () => ScriptedLlmResponder.LoadOverrides(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));
        act.Should().Throw<FileNotFoundException>(
            "a test pointing at a wrong script path must never silently run the default script");
    }

    [Test]
    public void Respond_KeyResolution_IsTierSplitOnDocumentType()
    {
        // 2026-08-13 correction #2 (engine-driven E2E run 34): resolution is
        // TIER-SPLIT on documentType, and the tiers never cross. A typed call
        // resolves qualified → @{doc} → registry example; a documentType-less
        // call resolves the bare cell only. History: the first correction let
        // the bare cell outrank @{doc} (reviewer calls then carried the
        // SUBJECT's type — 48× VALIDATED.FAILED, run 22); after
        // SingleReviewerWorkflow started declaring documentType='review', the
        // bare-over-@doc rule re-created the same failure in reverse — the TDD
        // single-shot cell 'tester/write-tests' (free-text test code)
        // intercepted the test-spec PRODUCER's documentType='test-spec' call.
        var overrides = new Dictionary<string, string>
        {
            ["architect/plan-system-design@plan"] = "QUALIFIED",
            ["@plan"] = "TYPE-DEFAULT",
            ["architect/plan-system-design"] = "BARE",
        };
        var responder = new ScriptedLlmResponder(overrides);

        responder.Respond(new ScriptedLlmCall("scripted", "architect", "plan-system-design", "plan", "m", "c"))
            .ResponseText.Should().Be("QUALIFIED");

        var withoutQualified = new ScriptedLlmResponder(new Dictionary<string, string>
        {
            ["@plan"] = "TYPE-DEFAULT",
            ["architect/plan-system-design"] = "BARE",
        });
        withoutQualified.Respond(new ScriptedLlmCall("scripted", "architect", "plan-system-design", "plan", "m", "c"))
            .ResponseText.Should().Be("TYPE-DEFAULT",
                "a TYPED call must be answered with a document of its type — the bare "
                + "(free-form) cell must never intercept it");

        var typeDefaultOnly = new ScriptedLlmResponder(new Dictionary<string, string>
        {
            ["@plan"] = "TYPE-DEFAULT",
        });
        typeDefaultOnly.Respond(new ScriptedLlmCall("scripted", "architect", "plan-system-design", "plan", "m", "c"))
            .ResponseText.Should().Be("TYPE-DEFAULT",
                "with no role/action cell the per-type default still serves");

        // No documentType ⇒ the bare cell serves.
        responder.Respond(new ScriptedLlmCall("scripted", "architect", "plan-system-design", null, "m", "c"))
            .ResponseText.Should().Be("BARE");

        // …and a documentType-less call never falls back to typed-tier cells.
        typeDefaultOnly.Respond(new ScriptedLlmCall("scripted", "architect", "plan-system-design", null, "m", "c"))
            .Success.Should().BeFalse("the tiers must not cross in either direction");
    }

    // =====================================================================
    // Runner dispatch — a scripted call opens NO HTTP socket
    // =====================================================================

    [Test]
    public async Task Runner_ScriptedProvider_ServesInProcess_NeverTouchingHttp()
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(new ThrowingHandler())); // any real send THROWS

        var runner = new InlineToolLoopRunner(
            NullLogger<InlineToolLoopRunner>.Instance, factory.Object, configuration: null,
            sanitizer: null, autonomyGate: new CatalogDefaultToolLoopAutonomyGate(),
            scriptedResponder: new ScriptedLlmResponder());

        var config = new LlmProviderConfig
        {
            Name = "scripted",
            ApiKey = "scripted-no-key",
            CallRole = "devops",
            CallAction = "deploy",
        };

        var result = await runner.RunAsync(
            "scripted", config, "any-model", "system", "user", 4096, 0.7,
            tools: null, enableToolLoop: false, new ToolLoopConfig(), "corr-x",
            repair: null, CancellationToken.None);

        result.Response.Success.Should().BeTrue();
        result.Response.ResponseText.Should().Be(ScriptedCycleLibrary.StageSuccess);
        result.Turns.Should().Be(1);
        result.InputTokens.Should().Be(0);
        result.OutputTokens.Should().Be(0);
    }

    [Test]
    public async Task Runner_ScriptedProvider_RunsTheRealRepairRingValidation()
    {
        var runner = new InlineToolLoopRunner(
            NullLogger<InlineToolLoopRunner>.Instance, httpClientFactory: null, configuration: null,
            sanitizer: null, autonomyGate: new CatalogDefaultToolLoopAutonomyGate(),
            scriptedResponder: new ScriptedLlmResponder());

        var config = new LlmProviderConfig
        {
            Name = "scripted",
            CallRole = "architect",
            CallAction = "plan-system-design",
        };

        var planType = DocumentTypeRegistry.Resolve("plan");
        var repair = new RepairRingPlan(
            "plan",
            text => planType.Validate(System.Text.Json.JsonDocument.Parse(text).RootElement),
            RepairEnabled: false,
            MaxRepairTurns: 0);

        var result = await runner.RunAsync(
            "scripted", config, "m", "system", "user", 4096, 0.7,
            tools: null, enableToolLoop: false, new ToolLoopConfig(), "corr-y",
            repair, CancellationToken.None);

        result.Response.Success.Should().BeTrue();
        result.ContentValid.Should().BeTrue(
            "the scripted plan payload must pass the REAL plan validator inside the 39-9 ring");
        result.RepairTurns.Should().Be(0);
    }

    // =====================================================================
    // Registration — opt-in only, structurally impossible in production
    // =====================================================================

    [Test]
    public void AddScriptedLlmProvider_FlagOff_IsAByteIdenticalNoOp()
    {
        var services = BaseServices();
        services.AddScriptedLlmProvider(Config());

        using var sp = services.BuildServiceProvider();
        sp.GetService<IScriptedLlmResponder>().Should().BeNull(
            "the default (no flag) must never register the test provider");
        sp.GetRequiredService<IProviderCredentialResolver>().Should().BeOfType<FakeResolver>(
            "the credential resolver must be untouched when the flag is off");
        new ProviderAllowlist(sp.GetRequiredService<IOptions<ProviderAllowlistOptions>>())
            .IsAllowed("scripted").Should().BeFalse();
    }

    [TestCase("Tamma:TenantSharedSecret", "hmac-secret")]
    [TestCase("ConnectionStrings:ControlPlane", "Host=cp;Database=t")]
    [TestCase("Tamma:Mode", "saas")]
    public void AddScriptedLlmProvider_FlagOnWithProductionSignal_RefusesToStart(
        string key, string value)
    {
        var services = BaseServices();
        var config = Config(
            ("Llm:EnableScriptedProvider", "true"),
            (key, value));

        var act = () => services.AddScriptedLlmProvider(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*refused*production*",
                "enabling the scripted provider on a production-shaped host must fail LOUD at startup");
    }

    [Test]
    public async Task AddScriptedLlmProvider_FlagOnCleanHost_WiresResponderAllowlistAndCredential()
    {
        var services = BaseServices();
        services.AddScriptedLlmProvider(Config(("Llm:EnableScriptedProvider", "true")));

        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IScriptedLlmResponder>().Should().BeOfType<ScriptedLlmResponder>();

        // The DI allowlist now admits the key (the shipped defaults are untouched —
        // pinned separately by ScriptedProviderPostureTests).
        new ProviderAllowlist(sp.GetRequiredService<IOptions<ProviderAllowlistOptions>>())
            .IsAllowed("scripted").Should().BeTrue();

        // Credential decoration: "scripted" answers the placeholder; every other
        // provider delegates to the inner resolver.
        var resolver = sp.GetRequiredService<IProviderCredentialResolver>();
        resolver.Should().BeOfType<ScriptedProviderCredentialResolver>();
        var scripted = await resolver.ResolveAsync(null, "scripted");
        scripted.ApiKey.Should().Be(ScriptedProviderCredentialResolver.PlaceholderKey);
        scripted.Source.Should().Be(CredentialSource.Platform);

        var delegated = await resolver.ResolveAsync(null, "anthropic");
        delegated.ApiKey.Should().Be("inner-key", "non-scripted providers must delegate untouched");
    }

    [Test]
    public void AddScriptedLlmProvider_WithoutCredentialResolution_FailsLoud()
    {
        var services = new ServiceCollection();
        var act = () => services.AddScriptedLlmProvider(
            Config(("Llm:EnableScriptedProvider", "true")));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddProviderCredentialResolution*");
    }

    [Test]
    public void AddScriptedLlmProvider_BadScriptPath_FailsAtRegistration()
    {
        var services = BaseServices();
        var act = () => services.AddScriptedLlmProvider(Config(
            ("Llm:EnableScriptedProvider", "true"),
            ("Llm:ScriptedProvider:ScriptPath", "/nonexistent/script.json")));
        act.Should().Throw<FileNotFoundException>();
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<IProviderCredentialResolver, FakeResolver>();
        return services;
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    private sealed class FakeResolver : IProviderCredentialResolver
    {
        public Task<ProviderCredential> ResolveAsync(
            Guid? tenantId, string providerName, CancellationToken ct = default) =>
            Task.FromResult(new ProviderCredential("inner-key", CredentialSource.Platform, "inner", null));

        public void Invalidate(Guid? tenantId, string providerName) { }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                $"A scripted-provider call attempted a real HTTP send to {request.RequestUri} — forbidden.");
    }
}
