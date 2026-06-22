using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.LlmCall.Models;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.Security;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-5 (T3) — <see cref="ManagedAgent"/> rule-2 composition. Every
/// collaborator is faked; assertions cover the compose ORDER, full
/// <see cref="AgentRunResult"/> population, the byok/platform cost branch, the
/// "exactly one terminal AGENT.RUN.* per run" invariant, every typed failure
/// path (each producing a run record, never a throw), and credential safety
/// (the key appears in no result / event payload).
/// </summary>
[TestFixture]
public class ManagedAgentTests
{
    private const string TestApiKey = "sk-super-secret-DO-NOT-LEAK-1234567890";

    private Mock<ISaaSProviderGate> _gate = null!;
    private Mock<IBudgetGuard> _budget = null!;
    private Mock<IAgentResolverService> _resolver = null!;
    private Mock<IProviderCredentialResolver> _credentials = null!;
    private Mock<IInlineToolLoopRunner> _runner = null!;
    private Mock<IProviderPricingService> _pricing = null!;
    private IProviderMarkupEngine _markup = null!;
    private RecordingUsageEmitter _usage = null!;
    private RecordingEventRepository _events = null!;
    private ManagedAgent _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _gate = new Mock<ISaaSProviderGate>(MockBehavior.Strict);
        _budget = new Mock<IBudgetGuard>(MockBehavior.Strict);
        _resolver = new Mock<IAgentResolverService>(MockBehavior.Strict);
        _credentials = new Mock<IProviderCredentialResolver>(MockBehavior.Strict);
        _runner = new Mock<IInlineToolLoopRunner>(MockBehavior.Strict);
        _pricing = new Mock<IProviderPricingService>(MockBehavior.Loose);
        _markup = new PassthroughProviderMarkupEngine();
        _usage = new RecordingUsageEmitter();
        _events = new RecordingEventRepository();

        _sut = new ManagedAgent(
            _gate.Object, _budget.Object, _resolver.Object, _credentials.Object,
            _runner.Object, _pricing.Object, _markup, _usage, _events,
            NullLogger<ManagedAgent>.Instance);
    }

    // -------------------------------------------------------------------
    // Happy path
    // -------------------------------------------------------------------

    [Test]
    public async Task RunAsync_HappyPath_ComposesInOrderAndPopulatesResult()
    {
        var tenantId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        SetupResolve(agentId, provider: "anthropic", model: "claude-sonnet-4");
        SetupGateAllow();
        SetupBudgetWithin();
        SetupCredential(CredentialSource.Platform);
        SetupRunnerSuccess(inTok: 100, outTok: 50, text: "done");
        _pricing.Setup(p => p.Compute("anthropic", "claude-sonnet-4", 100, 50)).Returns(0.0030m);

        var run = await _sut.RunAsync(Req(tenantId, role: "developer", action: "implement"));

        run.Success.Should().BeTrue();
        run.AgentId.Should().Be(agentId);
        run.Version.Should().Be(7);
        run.Provider.Should().Be("anthropic");
        run.Model.Should().Be("claude-sonnet-4");
        run.Role.Should().Be("developer");
        run.InputTokens.Should().Be(100);
        run.OutputTokens.Should().Be(50);
        run.CostUsd.Should().Be(0.0030m, "cost basis comes straight from IProviderPricingService.Compute");
        run.CredentialSource.Should().Be("platform");
        run.ResponseText.Should().Be("done");
        run.FailureCode.Should().BeNull();

        // Pricing was called with EXACTLY the runner's token counts.
        _pricing.Verify(p => p.Compute("anthropic", "claude-sonnet-4", 100, 50), Times.Once);

        // Exactly one STARTED + one terminal SUCCESS, no FAILED.
        _events.TypeCount(AgentRunEventTypes.Started).Should().Be(1);
        _events.TypeCount(AgentRunEventTypes.Success).Should().Be(1);
        _events.TypeCount(AgentRunEventTypes.Failed).Should().Be(0);
        TerminalCount().Should().Be(1, "exactly one terminal AGENT.RUN.* per run");

        // One usage record emitted with the provider cost basis.
        _usage.Records.Should().ContainSingle();
        _usage.Records[0].ProviderCostUsd.Should().Be(0.0030m);
    }

    [Test]
    public async Task RunAsync_StartedEvent_FiresBeforeRunner()
    {
        var agentId = Guid.NewGuid();
        var startedBeforeRun = false;
        SetupResolve(agentId, "anthropic", "claude-sonnet-4");
        SetupGateAllow();
        SetupBudgetWithin();
        SetupCredential(CredentialSource.Platform);
        _runner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<LlmProviderConfig>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<double>(), It.IsAny<IReadOnlyList<ResolvedTool>?>(), It.IsAny<bool>(),
                It.IsAny<ToolLoopConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, LlmProviderConfig, string, string, string, int, double,
                IReadOnlyList<ResolvedTool>?, bool, ToolLoopConfig, string, CancellationToken>(
                (_, _, _, _, _, _, _, _, _, _, _, _) =>
                {
                    startedBeforeRun = _events.TypeCount(AgentRunEventTypes.Started) == 1;
                    return Task.FromResult(SuccessLoop(1, 1, "x"));
                });
        _pricing.Setup(p => p.Compute(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(0m);

        await _sut.RunAsync(Req(Guid.NewGuid(), "developer"));

        startedBeforeRun.Should().BeTrue("AGENT.RUN.STARTED must be emitted before the tool loop runs");
    }

    // -------------------------------------------------------------------
    // BYOK vs platform cost branch (rule 7)
    // -------------------------------------------------------------------

    [Test]
    public async Task RunAsync_Byok_PriceIsZero_ProviderCostUnchanged()
    {
        SetupResolve(Guid.NewGuid(), "anthropic", "claude-sonnet-4");
        SetupGateAllow();
        SetupBudgetWithin();
        SetupCredential(CredentialSource.Byok);
        SetupRunnerSuccess(10, 5, "ok");
        _pricing.Setup(p => p.Compute("anthropic", "claude-sonnet-4", 10, 5)).Returns(0.5m);

        await _sut.RunAsync(Req(Guid.NewGuid(), "developer"));

        _usage.Records.Should().ContainSingle();
        var rec = _usage.Records[0];
        rec.CredentialSource.Should().Be("byok");
        rec.ProviderCostUsd.Should().Be(0.5m, "the raw basis is identical regardless of source");
        rec.PriceUsd.Should().Be(0m, "BYOK token price is 0 (rule 7)");
    }

    [Test]
    public async Task RunAsync_Platform_PriceIsMarkedUp_ProviderCostUnchanged()
    {
        SetupResolve(Guid.NewGuid(), "anthropic", "claude-sonnet-4");
        SetupGateAllow();
        SetupBudgetWithin();
        SetupCredential(CredentialSource.Platform);
        SetupRunnerSuccess(10, 5, "ok");
        _pricing.Setup(p => p.Compute("anthropic", "claude-sonnet-4", 10, 5)).Returns(0.5m);

        await _sut.RunAsync(Req(Guid.NewGuid(), "developer"));

        var rec = _usage.Records.Should().ContainSingle().Subject;
        rec.CredentialSource.Should().Be("platform");
        rec.ProviderCostUsd.Should().Be(0.5m, "identical basis on both legs");
        rec.PriceUsd.Should().Be(0.5m, "platform leg bills the basis (interim passthrough until 34-5)");
    }

    // -------------------------------------------------------------------
    // Failure paths — each yields a run record + exactly one terminal FAILED
    // -------------------------------------------------------------------

    [Test]
    public async Task RunAsync_GateDeniesCliProvider_FailsWith_SaasProviderNotAllowed()
    {
        SetupResolve(Guid.NewGuid(), "claude-code", "n/a");
        _gate.Setup(g => g.InspectAsync(It.IsAny<ProviderGateContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderGateDecision(
                false, ProviderGateOutcome.SaasProviderNotAllowed, "cli in saas", null, 400));

        var run = await _sut.RunAsync(Req(Guid.NewGuid(), "developer"));

        run.Success.Should().BeFalse();
        run.FailureCode.Should().Be(AgentRunFailureCodes.SaasProviderNotAllowed);
        run.HttpStatusCode.Should().Be(400);
        // Provider never called; credential never resolved.
        _runner.VerifyNoOtherCalls();
        _credentials.VerifyNoOtherCalls();
        AssertExactlyOneTerminalFailed();
    }

    [Test]
    public async Task RunAsync_GateDeniesEntitlement_FailsWith_TenantNotEntitled()
    {
        SetupResolve(Guid.NewGuid(), "anthropic", "claude-sonnet-4");
        _gate.Setup(g => g.InspectAsync(It.IsAny<ProviderGateContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderGateDecision(
                false, ProviderGateOutcome.TenantNotEntitled, "not entitled", ProviderAuthModel.ApiKey, 403));

        var run = await _sut.RunAsync(Req(Guid.NewGuid(), "developer"));

        run.Success.Should().BeFalse();
        run.FailureCode.Should().Be(AgentRunFailureCodes.TenantNotEntitled);
        run.HttpStatusCode.Should().Be(403);
        _runner.VerifyNoOtherCalls();
        AssertExactlyOneTerminalFailed();
    }

    [Test]
    public async Task RunAsync_CredentialUnavailable_FailsClosed_ProviderNeverCalled()
    {
        SetupResolve(Guid.NewGuid(), "anthropic", "claude-sonnet-4");
        SetupGateAllow();
        SetupBudgetWithin();
        _credentials
            .Setup(c => c.ResolveAsync(It.IsAny<Guid?>(), "anthropic", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Tamma.Core.TammaError(
                "PROVIDER_CREDENTIAL_UNAVAILABLE", "no key",
                retryable: false, severity: Tamma.Core.TammaErrorSeverity.High));

        var run = await _sut.RunAsync(Req(Guid.NewGuid(), "developer"));

        run.Success.Should().BeFalse();
        run.FailureCode.Should().Be(AgentRunFailureCodes.CredentialUnavailable);
        _runner.VerifyNoOtherCalls();
        AssertExactlyOneTerminalFailed();
    }

    [Test]
    public async Task RunAsync_OverBudget_FailsWith_BudgetExceeded_LoopNeverInvoked()
    {
        SetupResolve(Guid.NewGuid(), "anthropic", "claude-sonnet-4");
        SetupGateAllow();
        _budget.Setup(b => b.IsWithinBudgetAsync(It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var run = await _sut.RunAsync(Req(Guid.NewGuid(), "developer"));

        run.Success.Should().BeFalse();
        run.FailureCode.Should().Be(AgentRunFailureCodes.BudgetExceeded);
        _runner.VerifyNoOtherCalls();
        _credentials.VerifyNoOtherCalls();
        AssertExactlyOneTerminalFailed();
    }

    [Test]
    public async Task RunAsync_ProviderError_PreservesHttpStatus429()
    {
        SetupResolve(Guid.NewGuid(), "anthropic", "claude-sonnet-4");
        SetupGateAllow();
        SetupBudgetWithin();
        SetupCredential(CredentialSource.Platform);
        _runner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<LlmProviderConfig>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<double>(), It.IsAny<IReadOnlyList<ResolvedTool>?>(), It.IsAny<bool>(),
                It.IsAny<ToolLoopConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InlineToolLoopResult
            {
                Response = new NormalizedLlmResponse
                {
                    Success = false,
                    HttpStatusCode = 429,
                    ErrorMessage = "rate limited",
                },
                InputTokens = 7,
                OutputTokens = 0,
                Turns = 1,
                Exhausted = false,
            });

        var run = await _sut.RunAsync(Req(Guid.NewGuid(), "developer"));

        run.Success.Should().BeFalse();
        run.FailureCode.Should().Be(AgentRunFailureCodes.ProviderError);
        run.HttpStatusCode.Should().Be(429, "the upstream status is preserved for RetryCheck/the breaker");
        run.InputTokens.Should().Be(7, "usage accrued before the failure is preserved");
        AssertExactlyOneTerminalFailed();
    }

    [Test]
    public async Task RunAsync_LoopExhausted_FailsWith_LoopExhausted()
    {
        SetupResolve(Guid.NewGuid(), "anthropic", "claude-sonnet-4");
        SetupGateAllow();
        SetupBudgetWithin();
        SetupCredential(CredentialSource.Platform);
        _runner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<LlmProviderConfig>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<double>(), It.IsAny<IReadOnlyList<ResolvedTool>?>(), It.IsAny<bool>(),
                It.IsAny<ToolLoopConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InlineToolLoopResult
            {
                // success but exhausted with no usable text
                Response = new NormalizedLlmResponse { Success = true, ResponseText = null, HttpStatusCode = 200 },
                InputTokens = 50,
                OutputTokens = 10,
                Turns = 20,
                Exhausted = true,
            });

        var run = await _sut.RunAsync(Req(Guid.NewGuid(), "developer"));

        run.Success.Should().BeFalse();
        run.FailureCode.Should().Be(AgentRunFailureCodes.LoopExhausted);
        run.InputTokens.Should().Be(50, "accrued tokens preserved");
        AssertExactlyOneTerminalFailed();
    }

    [Test]
    public async Task RunAsync_ResolverThrowsNoEnabledDefault_FailsWith_AgentUnresolved_NonRetryable()
    {
        _resolver
            .Setup(r => r.ResolveForRoleAsync("developer", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Tamma.Core.TammaError(
                "AGENT.RESOLVE.NO_ENABLED_DEFAULT", "nothing enabled",
                retryable: false, severity: Tamma.Core.TammaErrorSeverity.High));

        var run = await _sut.RunAsync(Req(Guid.NewGuid(), "developer"));

        run.Success.Should().BeFalse("a resolution failure produces a run record, never a throw (AC10)");
        run.FailureCode.Should().Be(AgentRunFailureCodes.AgentUnresolved,
            "a config failure is NOT a credential/provider problem");
        run.HttpStatusCode.Should().Be(422,
            "422 is not in RetryCheck's transient set {0,429,502,503,504}, so the engine won't retry a config failure");
        new[] { 0, 429, 502, 503, 504 }.Should().NotContain(run.HttpStatusCode!.Value,
            "AGENT_UNRESOLVED must be non-retryable");
        _runner.VerifyNoOtherCalls();
        AssertExactlyOneTerminalFailed();
    }

    [Test]
    public async Task RunAsync_ResolverThrowsUnknownRole_FailsWith_AgentUnresolved_NotProviderError()
    {
        // An unknown role surfaces as an ArgumentException from the resolver — a
        // config/validation error, NOT a provider failure.
        _resolver
            .Setup(r => r.ResolveForRoleAsync("bogus-role", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("unknown role 'bogus-role'", "role"));

        var run = await _sut.RunAsync(Req(Guid.NewGuid(), "bogus-role"));

        run.Success.Should().BeFalse();
        run.FailureCode.Should().Be(AgentRunFailureCodes.AgentUnresolved);
        run.FailureCode.Should().NotBe(AgentRunFailureCodes.ProviderError,
            "an unknown role is a config error, not a provider failure");
        run.HttpStatusCode.Should().Be(422);
        _runner.VerifyNoOtherCalls();
        AssertExactlyOneTerminalFailed();
    }

    // -------------------------------------------------------------------
    // Credential safety on the exception-message path (defensive guard)
    // -------------------------------------------------------------------

    [Test]
    public async Task RunAsync_RunnerThrowsWithKeyInMessage_KeyNeverLeaksAnywhere()
    {
        SetupResolve(Guid.NewGuid(), "anthropic", "claude-sonnet-4");
        SetupGateAllow();
        SetupBudgetWithin();
        SetupCredential(CredentialSource.Platform);
        // A (theoretical) runner that leaks the key into its exception message.
        // The runner contract forbids this, but credential safety is load-bearing:
        // the key must NOT escape into the caller-facing FailureReason / response /
        // any emitted event payload even if a collaborator misbehaves.
        _runner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<LlmProviderConfig>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<double>(), It.IsAny<IReadOnlyList<ResolvedTool>?>(), It.IsAny<bool>(),
                It.IsAny<ToolLoopConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"upstream rejected key {TestApiKey} (401)"));

        var run = await _sut.RunAsync(Req(Guid.NewGuid(), "developer"));

        run.Success.Should().BeFalse();
        run.FailureCode.Should().Be(AgentRunFailureCodes.ProviderError);

        // The key must appear nowhere the caller or audit trail can read.
        (run.FailureReason ?? string.Empty).Should().NotContain(TestApiKey,
            "the runner's exception message must not leak the key into the caller-facing reason");

        var response = new LlmCallResponseMapper().ToResponse(run);
        System.Text.Json.JsonSerializer.Serialize(response).Should().NotContain(TestApiKey,
            "the projected LlmCallResponse must not carry the key");

        var runJson = System.Text.Json.JsonSerializer.Serialize(run);
        runJson.Should().NotContain(TestApiKey);
        foreach (var evt in _events.Appended)
        {
            (evt.Tags + evt.Data + evt.Metadata).Should().NotContain(TestApiKey);
        }

        AssertExactlyOneTerminalFailed();
    }

    [Test]
    public void RunAsync_NullRequest_Throws()
    {
        var act = async () => await _sut.RunAsync(null!);
        act.Should().ThrowAsync<ArgumentNullException>();
    }

    // -------------------------------------------------------------------
    // Credential safety — the key never escapes
    // -------------------------------------------------------------------

    [Test]
    public async Task RunAsync_ApiKey_NeverAppearsInResultOrEvents()
    {
        SetupResolve(Guid.NewGuid(), "anthropic", "claude-sonnet-4");
        SetupGateAllow();
        SetupBudgetWithin();
        SetupCredential(CredentialSource.Platform);
        SetupRunnerSuccess(10, 5, "ok");
        _pricing.Setup(p => p.Compute(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(0m);

        var run = await _sut.RunAsync(Req(Guid.NewGuid(), "developer"));

        // Serialize the whole producer record + every emitted event payload and
        // assert the key fragment appears nowhere.
        var resultJson = System.Text.Json.JsonSerializer.Serialize(run);
        resultJson.Should().NotContain(TestApiKey);
        foreach (var evt in _events.Appended)
        {
            (evt.Tags + evt.Data + evt.Metadata).Should().NotContain(TestApiKey);
        }
    }

    // -------------------------------------------------------------------
    // setup helpers
    // -------------------------------------------------------------------

    private static ManagedAgentRequest Req(Guid? tenantId, string role, string? action = null) => new()
    {
        TenantId = tenantId,
        Role = role,
        Action = action,
        Prompt = "do the thing",
        CorrelationId = "corr-1",
        Params = new LlmCallParams { MaxTokens = 4096, Temperature = 0.7 },
    };

    private void SetupResolve(Guid agentId, string provider, string model)
    {
        _resolver
            .Setup(r => r.ResolveForRoleAsync("developer", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedAgentConfig
            {
                Role = "developer",
                Handle = "tamma-developer",
                Provider = provider,
                Model = model,
                Temperature = 0.7,
                MaxTokens = 4096,
                TokenBudget = 100_000,
                SystemPrompt = "You are a developer.",
                AgentId = agentId,
                AgentVersion = 7,
                Source = "system-public",
            });
    }

    private void SetupGateAllow() =>
        _gate.Setup(g => g.InspectAsync(It.IsAny<ProviderGateContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderGateDecision.Allow(ProviderAuthModel.ApiKey));

    private void SetupBudgetWithin() =>
        _budget.Setup(b => b.IsWithinBudgetAsync(It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

    private void SetupCredential(CredentialSource source) =>
        _credentials.Setup(c => c.ResolveAsync(It.IsAny<Guid?>(), "anthropic", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderCredential(TestApiKey, source, "platform:anthropic/api-key", null));

    private void SetupRunnerSuccess(int inTok, int outTok, string text) =>
        _runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<LlmProviderConfig>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<double>(), It.IsAny<IReadOnlyList<ResolvedTool>?>(), It.IsAny<bool>(),
                It.IsAny<ToolLoopConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessLoop(inTok, outTok, text));

    private static InlineToolLoopResult SuccessLoop(int inTok, int outTok, string text) => new()
    {
        Response = new NormalizedLlmResponse
        {
            Success = true,
            ResponseText = text,
            HttpStatusCode = 200,
            PromptTokens = inTok,
            CompletionTokens = outTok,
        },
        InputTokens = inTok,
        OutputTokens = outTok,
        Turns = 1,
        Exhausted = false,
    };

    private int TerminalCount() =>
        _events.TypeCount(AgentRunEventTypes.Success) + _events.TypeCount(AgentRunEventTypes.Failed);

    private void AssertExactlyOneTerminalFailed()
    {
        _events.TypeCount(AgentRunEventTypes.Failed).Should().Be(1);
        _events.TypeCount(AgentRunEventTypes.Success).Should().Be(0);
        TerminalCount().Should().Be(1, "exactly one terminal AGENT.RUN.* per run");
    }

    // -------------------------------------------------------------------
    // fakes
    // -------------------------------------------------------------------

    private sealed class RecordingUsageEmitter : IUsageEmitter
    {
        public List<UsageRecord> Records { get; } = new();

        public Task EmitAsync(UsageRecord record, CancellationToken ct = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEventRepository : IEventRepository
    {
        public ConcurrentBag<DomainEvent> Appended { get; } = new();

        public int TypeCount(string type) => Appended.Count(e => e.Type == type);

        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            Appended.Add(evt);
            return Task.FromResult(evt);
        }

        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
            => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
            => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }
}
