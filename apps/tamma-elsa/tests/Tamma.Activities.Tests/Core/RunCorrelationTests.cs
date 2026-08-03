using System.Threading;
using System.Threading.Tasks;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Requests;
using Elsa.Workflows.Runtime.Responses;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Core;

namespace Tamma.Activities.Tests.Core;

/// <summary>
/// Story 43-14 (D5, AC4) — the ambient run-correlation holder and the dispatcher
/// decorator that propagates it to sub-workflows with zero per-dispatch edits.
/// </summary>
[TestFixture]
public class RunCorrelationTests
{
    [TearDown]
    public void Clear() => RunCorrelation.Current = null;

    [Test]
    public async Task WithAsync_SetsThenRestoresTheAmbient()
    {
        RunCorrelation.Current.Should().BeNull();
        string? seenInside = null;
        await RunCorrelation.WithAsync("run-42", () =>
        {
            seenInside = RunCorrelation.Current;
            return ValueTask.CompletedTask;
        });
        seenInside.Should().Be("run-42");
        RunCorrelation.Current.Should().BeNull("the ambient is restored on exit");
    }

    [Test]
    public async Task Dispatcher_StampsANullCorrelationFromTheAmbient()
    {
        var inner = new CapturingDispatcher();
        var decorator = new CorrelationPropagatingWorkflowDispatcher(inner);

        await RunCorrelation.WithAsync("cycle-instance-1", async () =>
        {
            var request = new DispatchWorkflowDefinitionRequest("some-sub-workflow");
            await decorator.DispatchAsync(request, new DispatchWorkflowOptions());
        });

        inner.LastDefinitionRequest!.CorrelationId.Should().Be("cycle-instance-1",
            "a sub-workflow dispatched inside a run inherits the run correlation");
    }

    [Test]
    public async Task Dispatcher_NeverOverwritesAnExplicitCorrelation()
    {
        var inner = new CapturingDispatcher();
        var decorator = new CorrelationPropagatingWorkflowDispatcher(inner);

        await RunCorrelation.WithAsync("cycle-instance-1", async () =>
        {
            var request = new DispatchWorkflowDefinitionRequest("some-sub-workflow")
            {
                CorrelationId = "explicit-corr",
            };
            await decorator.DispatchAsync(request, new DispatchWorkflowOptions());
        });

        inner.LastDefinitionRequest!.CorrelationId.Should().Be("explicit-corr",
            "an explicitly-set correlation is never overridden");
    }

    [Test]
    public async Task Dispatcher_PassesThrough_WhenNoAmbient()
    {
        var inner = new CapturingDispatcher();
        var decorator = new CorrelationPropagatingWorkflowDispatcher(inner);

        var request = new DispatchWorkflowDefinitionRequest("some-sub-workflow");
        await decorator.DispatchAsync(request, new DispatchWorkflowOptions());

        inner.LastDefinitionRequest!.CorrelationId.Should().BeNull(
            "outside a run there is nothing to stamp; Seam C's route: fallback stands");
    }

    private sealed class CapturingDispatcher : IWorkflowDispatcher
    {
        public DispatchWorkflowDefinitionRequest? LastDefinitionRequest { get; private set; }

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchWorkflowDefinitionRequest request, DispatchWorkflowOptions options,
            CancellationToken cancellationToken = default)
        {
            LastDefinitionRequest = request;
            return Task.FromResult(new DispatchWorkflowResponse(null));
        }

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchWorkflowInstanceRequest request, DispatchWorkflowOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DispatchWorkflowResponse(null));

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchTriggerWorkflowsRequest request, DispatchWorkflowOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DispatchWorkflowResponse(null));

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchResumeWorkflowsRequest request, DispatchWorkflowOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DispatchWorkflowResponse(null));
    }
}
