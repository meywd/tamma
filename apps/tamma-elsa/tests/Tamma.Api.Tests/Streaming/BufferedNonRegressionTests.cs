using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.LlmCall.Models;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.Security;
using Tamma.Api.Services.Streaming;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Streaming;

/// <summary>
/// Story 32-23 (AC6, THE load-bearing invariant) — the buffered
/// <see cref="ManagedAgent"/> run is IDENTICAL whether 0 or N tap subscribers are
/// attached, and a streaming failure NEVER faults or alters the buffered run. The
/// tap can only ever be a decoupled mirror of the engine's source-of-truth call.
/// </summary>
[TestFixture]
public class BufferedNonRegressionTests
{
    private const string TestApiKey = "sk-secret-never-in-a-frame-000";

    [Test]
    public async Task BufferedResult_IsIdentical_With0_vs_N_Subscribers()
    {
        var bus = new LlmRunStreamBus();

        // Run with ZERO subscribers.
        var agent0 = BuildAgent(bus);
        var result0 = await agent0.RunAsync(Req("corr-nonreg"));

        // Run again with N subscribers attached to the SAME correlationId's bus.
        var agentN = BuildAgent(bus);
        using var s1 = bus.Subscribe("corr-nonreg");
        using var s2 = bus.Subscribe("corr-nonreg");
        var resultN = await agentN.RunAsync(Req("corr-nonreg"));

        // Everything the engine keys off is identical; only wall-clock DurationMs
        // differs between the two invocations (it is not a function of subscribers).
        resultN.Should().BeEquivalentTo(result0, opts => opts.Excluding(r => r.DurationMs));

        // The mapped wire response the engine consumes is also unaffected.
        var mapper = new LlmCallResponseMapper();
        var wire0 = mapper.ToResponse(result0);
        var wireN = mapper.ToResponse(resultN);
        wireN.Should().BeEquivalentTo(wire0, opts => opts.Excluding(r => r.DurationMs));

        result0.Success.Should().BeTrue();
        resultN.Success.Should().BeTrue();
    }

    [Test]
    public async Task PublishThatThrows_IsSwallowed_RunStillSucceeds()
    {
        // A misbehaving bus that throws on publish must NEVER fault the run — the
        // producer-side publish is wrapped log-and-swallow (AC5/AC6).
        var throwingBus = new ThrowingRunStreamBus();
        var agent = BuildAgent(throwingBus);

        var act = async () => await agent.RunAsync(Req("corr-throw"));

        var result = await act.Should().NotThrowAsync();
        result.Which.Success.Should().BeTrue("a streaming failure never alters the buffered run");
    }

    [Test]
    public async Task Run_PublishesTerminalFinalFrame_ToSubscribers()
    {
        var bus = new LlmRunStreamBus();
        var agent = BuildAgent(bus);
        using var sub = bus.Subscribe("corr-final");

        await agent.RunAsync(Req("corr-final"));

        // The run publishes exactly one terminal `final` frame carrying the
        // key-free turn summary; the channel completes on it.
        var frames = new List<RunStreamFrame>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var f in sub.Reader.ReadAllAsync(cts.Token))
        {
            frames.Add(f);
        }

        frames.Should().ContainSingle();
        frames[0].Type.Should().Be(RunStreamFrameType.Final);
        var json = System.Text.Json.JsonSerializer.Serialize(RunStreamFrameScrubber.Scrub(frames[0]));
        json.Should().Contain("\"success\":true");
        json.Should().NotContain(TestApiKey, "the final frame is key-free (AC9)");
    }

    // -------------------------------------------------------------------
    // builders / fakes
    // -------------------------------------------------------------------

    private static ManagedAgent BuildAgent(ILlmRunStreamBus bus)
    {
        var gate = new Mock<ISaaSProviderGate>();
        gate.Setup(g => g.InspectAsync(It.IsAny<ProviderGateContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderGateDecision.Allow(ProviderAuthModel.ApiKey));

        var budget = new Mock<IBudgetGuard>();
        budget.Setup(b => b.IsWithinBudgetAsync(It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var resolver = new Mock<IAgentResolverService>();
        resolver.Setup(r => r.ResolveForRoleAsync("developer", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
                AgentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                AgentVersion = 3,
                Source = "system-public",
            });

        var credentials = new Mock<IProviderCredentialResolver>();
        credentials.Setup(c => c.ResolveAsync(It.IsAny<Guid?>(), "anthropic", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderCredential(TestApiKey, CredentialSource.Platform, "platform:anthropic/api-key", null));

        var runner = new Mock<IInlineToolLoopRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<LlmProviderConfig>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<double>(), It.IsAny<IReadOnlyList<ResolvedTool>?>(), It.IsAny<bool>(),
                It.IsAny<ToolLoopConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InlineToolLoopResult
            {
                Response = new NormalizedLlmResponse
                {
                    Success = true,
                    ResponseText = "done",
                    HttpStatusCode = 200,
                    PromptTokens = 100,
                    CompletionTokens = 50,
                },
                InputTokens = 100,
                OutputTokens = 50,
                Turns = 1,
                Exhausted = false,
            });

        var pricing = new Mock<IProviderPricingService>();
        pricing.Setup(p => p.Compute(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(0.003m);

        return new ManagedAgent(
            gate.Object, budget.Object, resolver.Object, credentials.Object,
            runner.Object, pricing.Object, new PassthroughProviderMarkupEngine(),
            new NoOpUsageEmitter(), new NoOpEventRepository(),
            NullLogger<ManagedAgent>.Instance,
            sanitizer: null, trail: null, runStreamBus: bus);
    }

    private static ManagedAgentRequest Req(string correlationId) => new()
    {
        TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Role = "developer",
        Prompt = "do the thing",
        CorrelationId = correlationId,
        Params = new LlmCallParams { MaxTokens = 4096, Temperature = 0.7 },
    };

    private sealed class ThrowingRunStreamBus : ILlmRunStreamBus
    {
        public ValueTask PublishAsync(string correlationId, RunStreamFrame frame, CancellationToken ct = default)
            => throw new InvalidOperationException("bus is down");

        public IRunStreamSubscription Subscribe(string correlationId)
            => throw new NotSupportedException();

        public int SubscriberCount(string correlationId) => 0;
    }

    private sealed class NoOpUsageEmitter : IUsageEmitter
    {
        public Task EmitAsync(UsageRecord record, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpEventRepository : IEventRepository
    {
        public ConcurrentBag<DomainEvent> Appended { get; } = new();
        public Task<DomainEvent> AppendAsync(DomainEvent evt) { Appended.Add(evt); return Task.FromResult(evt); }
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
            => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }
}
