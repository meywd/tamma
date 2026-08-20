using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Requests;
using Elsa.Workflows.Runtime.Responses;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Activities.Core;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// When the restart dispatch gives up, the loop has STOPPED — and the audit trail has to
/// say so.
///
/// <para><b>What was wrong.</b> The failure was swallowed so the instance would not fault
/// (correct — faulting adds nothing once the successor does not exist), but swallowing it
/// meant <see cref="TammaAsyncActivity"/> went on to emit
/// <c>ADL.SELF.DISPATCH.COMPLETED</c> with <c>status=success</c>. The one durable record of
/// the moment the autonomous loop died claimed a successful restart, and the only honest
/// signal was a Critical line in a rotating log file on the VPS.</para>
/// </summary>
[TestFixture]
public class AdlLoopStoppedSignalTests
{
    [Test]
    public async Task AGivenUpDispatch_EmitsALoudDurableEvent()
    {
        var result = await RunAsync(new ThrowingDispatcher { AlwaysThrow = true });

        var evt = Events(result).SingleOrDefault(e => e.EventType == AdlLoopEvents.SelfDispatchFailed);

        evt.Should().NotBeNull(
            "a Critical log line nobody is tailing is not a signal — the stop has to be in the "
            + "event stream an operator and the alert rules can query");
        evt!.Status.Should().Be("error");
        evt.Tags.Should().ContainKey("loopStopped").WhoseValue.Should().Be("true",
            "'is the loop dead right now' must be one tag filter over domain_events");
    }

    [Test]
    public async Task AGivenUpDispatch_DoesNotClaimItRestartedTheLoop()
    {
        var result = await RunAsync(new ThrowingDispatcher { AlwaysThrow = true });

        var completed = Events(result).Single(e => e.EventType == AdlLoopEvents.SelfDispatch + ".COMPLETED");
        completed.Data["dispatched"].Should().Be(false,
            "the end data is the audit record; reporting a dispatch that never happened is worse "
            + "than reporting nothing");
    }

    [Test]
    public async Task ASuccessfulDispatch_ReportsTheRestart_andEmitsNoFailure()
    {
        var result = await RunAsync(new ThrowingDispatcher());

        Events(result).Should().NotContain(e => e.EventType == AdlLoopEvents.SelfDispatchFailed);
        Events(result).Single(e => e.EventType == AdlLoopEvents.SelfDispatch + ".COMPLETED")
            .Data["dispatched"].Should().Be(true);
    }

    [Test]
    public async Task TheLiveConfig_IsRememberedBeforeDispatching_soAWatchdogCanReArmWithIt()
    {
        var cache = new AdlLoopConfigCache();
        const string config = """{"repository":"owner/repo"}""";

        await RunAsync(new ThrowingDispatcher { AlwaysThrow = true }, cache, config);

        cache.Last.Should().Be(config,
            "if the dispatch fails, the successor instance — the only other copy of the running "
            + "config — never exists, so it has to be captured BEFORE the attempt");
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static IReadOnlyList<TammaEvent> Events(RunWorkflowResult result)
        => result.WorkflowExecutionContext.TransientProperties
            .TryGetValue(EventDrain.EventsKey, out var raw) && raw is List<TammaEvent> list
            ? list
            : Array.Empty<TammaEvent>();

    private static async Task<RunWorkflowResult> RunAsync(
        IWorkflowDispatcher dispatcher,
        AdlLoopConfigCache? cache = null,
        string configJson = "{}")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElsa(elsa => elsa.AddActivity<DispatchAdlActivity>());
        services.AddSingleton(dispatcher);
        services.AddSingleton(cache ?? new AdlLoopConfigCache());

        var definitionService = new Moq.Mock<Elsa.Workflows.Management.IWorkflowDefinitionService>();
        definitionService
            .Setup(d => d.FindWorkflowDefinitionAsync(
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<Elsa.Common.Models.VersionOptions>(),
                Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, Elsa.Common.Models.VersionOptions _, CancellationToken _) =>
                new Elsa.Workflows.Management.Entities.WorkflowDefinition { Id = id, DefinitionId = id });
        services.AddSingleton(definitionService.Object);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();

        var activity = new DispatchAdlActivity { ConfigJson = new Input<string>(configJson) };
        return await runner.RunAsync(
            new SingleActivityWorkflow(activity), new RunWorkflowOptions(), CancellationToken.None);
    }

    private sealed class SingleActivityWorkflow(IActivity activity) : WorkflowBase
    {
        protected override void Build(IWorkflowBuilder builder) => builder.Root = activity;
    }

    private sealed class ThrowingDispatcher : IWorkflowDispatcher
    {
        public bool AlwaysThrow { get; set; }
        public List<DispatchWorkflowDefinitionRequest> Dispatched { get; } = new();

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchWorkflowDefinitionRequest request, DispatchWorkflowOptions? options = default,
            CancellationToken cancellationToken = default)
        {
            if (AlwaysThrow) throw new InvalidOperationException("broker down");
            Dispatched.Add(request);
            return Task.FromResult(new DispatchWorkflowResponse(Fault: null));
        }

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchWorkflowInstanceRequest request, DispatchWorkflowOptions? options = default,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DispatchWorkflowResponse(Fault: null));

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchTriggerWorkflowsRequest request, DispatchWorkflowOptions? options = default,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DispatchWorkflowResponse(Fault: null));

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchResumeWorkflowsRequest request, DispatchWorkflowOptions? options = default,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DispatchWorkflowResponse(Fault: null));
    }
}
