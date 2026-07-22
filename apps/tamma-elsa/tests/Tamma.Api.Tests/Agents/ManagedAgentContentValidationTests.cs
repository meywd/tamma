using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.LlmCall.Models;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.Security;
using Tamma.Core.Documents;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 39-9 (AC1, AC3, AC6, AC9) — the managed-layer orchestration of the repair
/// ring: the typed CONTENT_VALIDATION_FAILED (422-in-200, not transient), the payload
/// completeness (violations + history + tokens, no usage row), the LLM.* event trail
/// with its tags, the repaired-success path, and the gate-off behaviour. The runner is
/// a strict Moq returning a crafted <see cref="InlineToolLoopResult"/> so this exercises
/// ManagedAgent (not the runner internals — those are InlineToolLoopRunnerRepairTests).
/// </summary>
[TestFixture]
public class ManagedAgentContentValidationTests
{
    private const string DocType = "decomposition";

    private Mock<ISaaSProviderGate> _gate = null!;
    private Mock<IBudgetGuard> _budget = null!;
    private Mock<IAgentResolverService> _resolver = null!;
    private Mock<IProviderCredentialResolver> _credentials = null!;
    private Mock<IInlineToolLoopRunner> _runner = null!;
    private Mock<IProviderPricingService> _pricing = null!;
    private IProviderMarkupEngine _markup = null!;
    private RecordingUsageEmitter _usage = null!;
    private RecordingEventRepository _events = null!;

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

        SetupResolve();
        SetupGateAllow();
        SetupBudgetWithin();
        SetupCredential();
    }

    private ManagedAgent Build(RepairRingOptions opts) => new(
        _gate.Object, _budget.Object, _resolver.Object, _credentials.Object,
        _runner.Object, _pricing.Object, _markup, _usage, _events,
        NullLogger<ManagedAgent>.Instance,
        repairOptions: Options.Create(opts));

    // -------------------------------------------------------------------
    // Exhausted-invalid → typed CONTENT_VALIDATION_FAILED + event trail.
    // -------------------------------------------------------------------

    [Test]
    public async Task ExhaustedInvalid_TypedContentFailure_422_WithPayloadAndEvents()
    {
        var loop = LoopResult(
            contentValid: false, repairTurns: 1, text: "still-bad", inTok: 120, outTok: 40,
            history: new[]
            {
                new RepairTurnRecord(0, false, Vio("MISSING_FIELD", "Field 'x' required.")),
                new RepairTurnRecord(1, false, Vio("MISSING_FIELD", "Field 'x' required.")),
            });
        SetupRunner(loop);

        var sut = Build(EnabledFor(DocType)); // repair ON, cap 1
        var run = await sut.RunAsync(ReqWithDoc(issueId: "ISSUE-7"));

        run.Success.Should().BeFalse();
        run.FailureCode.Should().Be(AgentRunFailureCodes.ContentValidationFailed);
        run.HttpStatusCode.Should().Be(422, "a content failure is 422-style, not a transient provider error");
        run.RepairTurns.Should().Be(1);
        run.ContentValid.Should().BeFalse();
        run.ContentViolations.Should().ContainSingle().Which.Code.Should().Be("MISSING_FIELD");
        run.RepairHistory.Should().HaveCount(2);
        run.InputTokens.Should().Be(120, "token counts (incl. repair spend) ride the result");
        run.OutputTokens.Should().Be(40);

        _usage.Records.Should().BeEmpty("a failed run emits NO usage row (32-9 decision)");

        _events.TypeCount(AgentRunEventTypes.Failed).Should().Be(1, "exactly one terminal AGENT.RUN.FAILED");
        _events.TypeCount(AgentRunEventTypes.Success).Should().Be(0);
        _events.TypeCount(RepairRingEventTypes.ValidationFailed).Should().Be(2, "turn 0 and turn 1");
        _events.TypeCount(RepairRingEventTypes.RepairExhausted).Should().Be(1);
        _events.TypeCount(RepairRingEventTypes.RepairSucceeded).Should().Be(0);

        // Tags carry issueId / documentType / role / action / repairTurn.
        var vf = _events.OfType(RepairRingEventTypes.ValidationFailed).First();
        vf.Tags.Should().Contain("ISSUE-7").And.Contain(DocType).And.Contain("developer");
        vf.Tags.Should().Contain("\"repairTurn\"");
    }

    // -------------------------------------------------------------------
    // Repaired → success with RepairTurns == 1 + LLM.REPAIR.SUCCEEDED.
    // -------------------------------------------------------------------

    [Test]
    public async Task Repaired_Success_WithRepairSucceededEvent()
    {
        var loop = LoopResult(
            contentValid: true, repairTurns: 1, text: "fixed", inTok: 80, outTok: 20,
            history: new[]
            {
                new RepairTurnRecord(0, false, Vio("E", "bad")),
                new RepairTurnRecord(1, true, Array.Empty<DocumentViolation>()),
            });
        SetupRunner(loop);

        var sut = Build(EnabledFor(DocType));
        var run = await sut.RunAsync(ReqWithDoc(issueId: "ISSUE-9"));

        run.Success.Should().BeTrue();
        run.RepairTurns.Should().Be(1);
        run.ContentValid.Should().BeTrue();
        run.ResponseText.Should().Be("fixed");

        _events.TypeCount(AgentRunEventTypes.Success).Should().Be(1);
        _events.TypeCount(RepairRingEventTypes.ValidationFailed).Should().Be(1, "only turn 0 failed");
        _events.TypeCount(RepairRingEventTypes.RepairSucceeded).Should().Be(1);
        _events.TypeCount(RepairRingEventTypes.RepairExhausted).Should().Be(0);
    }

    // -------------------------------------------------------------------
    // Gate OFF → only VALIDATION.FAILED; plan handed to the runner is disabled.
    // -------------------------------------------------------------------

    [Test]
    public async Task GateOff_OnlyValidationFailed_PlanDisabled()
    {
        var loop = LoopResult(
            contentValid: false, repairTurns: 0, text: "bad", inTok: 10, outTok: 5,
            history: new[] { new RepairTurnRecord(0, false, Vio("E", "bad")) });
        var capturedPlan = SetupRunner(loop);

        var sut = Build(new RepairRingOptions()); // default: nothing enabled
        var run = await sut.RunAsync(ReqWithDoc(issueId: "ISSUE-1"));

        run.FailureCode.Should().Be(AgentRunFailureCodes.ContentValidationFailed);
        run.RepairTurns.Should().Be(0, "gate off ⇒ zero extra turns");

        capturedPlan().Should().NotBeNull();
        capturedPlan()!.RepairEnabled.Should().BeFalse("the plan handed to the runner is gated off (AC9)");

        _events.TypeCount(RepairRingEventTypes.ValidationFailed).Should().Be(1);
        _events.TypeCount(RepairRingEventTypes.RepairExhausted).Should().Be(0, "no exhaustion when gated off");
        _events.TypeCount(RepairRingEventTypes.RepairSucceeded).Should().Be(0);
    }

    // -------------------------------------------------------------------
    // No DocumentValidation → no LLM.* events, no new fields set.
    // -------------------------------------------------------------------

    [Test]
    public async Task NoDocumentValidation_NoRepairEvents_FieldsDefault()
    {
        var loop = LoopResult(contentValid: null, repairTurns: 0, text: "ok", inTok: 10, outTok: 5,
            history: Array.Empty<RepairTurnRecord>());
        SetupRunner(loop);

        var sut = Build(EnabledFor(DocType));
        var run = await sut.RunAsync(ReqNoDoc());

        run.Success.Should().BeTrue();
        run.ContentValid.Should().BeNull();
        run.RepairHistory.Should().BeNull();
        _events.TypeCount(RepairRingEventTypes.ValidationFailed).Should().Be(0);
        _events.TypeCount(RepairRingEventTypes.RepairSucceeded).Should().Be(0);
        _events.TypeCount(RepairRingEventTypes.RepairExhausted).Should().Be(0);
    }

    // -------------------------------------------------------------------
    // Mapper: content failure rides a 200 envelope; body status 422 (not transient).
    // -------------------------------------------------------------------

    [Test]
    public void Mapper_ContentFailure_RidesInside200_BodyStatus422()
    {
        var run = new AgentRunResult
        {
            Success = false,
            Role = "developer",
            CorrelationId = "c",
            FailureCode = AgentRunFailureCodes.ContentValidationFailed,
            FailureReason = "document failed validation",
            HttpStatusCode = 422,
            ContentValid = false,
            RepairTurns = 1,
            ContentViolations = new[] { new DocumentViolation("E", "bad") },
            RepairHistory = new[] { new RepairTurnRecord(0, false, Vio("E", "bad")) },
        };

        var http = new LlmCallResponseMapper().ToHttpResult(run);
        http.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LlmCallResponse>>();
        var body = ((Microsoft.AspNetCore.Http.HttpResults.Ok<LlmCallResponse>)http).Value!;

        body.Success.Should().BeFalse();
        body.HttpStatusCode.Should().Be(422, "422 is NOT in RetryCheck's transient set {0,429,502,503,504}");
        body.ContentValidation.Should().NotBeNull();
        body.ContentValidation!.Valid.Should().BeFalse();
        body.ContentValidation.RepairTurns.Should().Be(1);
        body.ContentValidation.Violations.Should().ContainSingle().Which.Code.Should().Be("E");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<DocumentViolation> Vio(string code, string message) =>
        new[] { new DocumentViolation(code, message) };

    private static RepairRingOptions EnabledFor(string key) =>
        new() { EnabledDocumentTypes = new[] { key } };

    private static InlineToolLoopResult LoopResult(
        bool? contentValid, int repairTurns, string text, int inTok, int outTok,
        IReadOnlyList<RepairTurnRecord> history) => new()
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
        ContentValid = contentValid,
        RepairTurns = repairTurns,
        RepairHistory = history,
    };

    /// <summary>Set up the strict runner mock to return <paramref name="loop"/>, and
    /// return an accessor for the RepairRingPlan the SUT handed the runner.</summary>
    private Func<RepairRingPlan?> SetupRunner(InlineToolLoopResult loop)
    {
        RepairRingPlan? captured = null;
        _runner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<LlmProviderConfig>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<double>(), It.IsAny<IReadOnlyList<ResolvedTool>?>(), It.IsAny<bool>(),
                It.IsAny<ToolLoopConfig>(), It.IsAny<string>(), It.IsAny<RepairRingPlan?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, LlmProviderConfig, string, string, string, int, double,
                IReadOnlyList<ResolvedTool>?, bool, ToolLoopConfig, string, RepairRingPlan?, CancellationToken>(
                (_, _, _, _, _, _, _, _, _, _, _, plan, _) =>
                {
                    captured = plan;
                    return Task.FromResult(loop);
                });
        return () => captured;
    }

    private static ManagedAgentRequest ReqWithDoc(string issueId) => new()
    {
        TenantId = Guid.NewGuid(),
        Role = "developer",
        Action = "issue-decomposition",
        Prompt = "decompose the issue",
        CorrelationId = "corr-cv",
        Params = new LlmCallParams { MaxTokens = 4096, Temperature = 0.7 },
        DocumentValidation = new DocumentContentValidation(DocType, _ => DocumentValidationResult.Valid()),
        IssueId = issueId,
    };

    private static ManagedAgentRequest ReqNoDoc() => new()
    {
        TenantId = Guid.NewGuid(),
        Role = "developer",
        Prompt = "do the thing",
        CorrelationId = "corr-nodoc",
        Params = new LlmCallParams { MaxTokens = 4096, Temperature = 0.7 },
    };

    private void SetupResolve() =>
        _resolver
            .Setup(r => r.ResolveForRoleAsync("developer", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedAgentConfig
            {
                Role = "developer",
                Handle = "tamma-developer",
                Provider = "anthropic",
                Model = "claude-sonnet-4",
                Temperature = 0.7,
                MaxTokens = 4096,
                TokenBudget = 100_000,
                SystemPrompt = "You are a developer.",
                AgentId = Guid.NewGuid(),
                AgentVersion = 7,
                Source = "system-public",
            });

    private void SetupGateAllow() =>
        _gate.Setup(g => g.InspectAsync(It.IsAny<ProviderGateContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderGateDecision.Allow(ProviderAuthModel.ApiKey));

    private void SetupBudgetWithin() =>
        _budget.Setup(b => b.IsWithinBudgetAsync(It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

    private void SetupCredential() =>
        _credentials.Setup(c => c.ResolveAsync(It.IsAny<Guid?>(), "anthropic", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderCredential("sk-test-key", CredentialSource.Platform,
                "platform:anthropic/api-key", null));

    // fakes
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
        public IEnumerable<DomainEvent> OfType(string type) => Appended.Where(e => e.Type == type);

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
