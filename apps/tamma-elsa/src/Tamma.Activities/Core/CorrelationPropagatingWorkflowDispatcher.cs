using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Requests;
using Elsa.Workflows.Runtime.Responses;

namespace Tamma.Activities.Core;

/// <summary>
/// Story 43-14 (Amendment 2-B, D5) — an <see cref="IWorkflowDispatcher"/>
/// decorator that stamps the ambient <see cref="RunCorrelation.Current"/> onto
/// every dispatched-definition request that does NOT already carry a
/// correlation. This is what makes a whole chain (cycle → sub-workflows → their
/// sub-workflows) share ONE ledger-visible correlation with ZERO per-dispatch
/// edits: <c>DispatchCycleActivity</c> seeds the cycle's correlation to the cycle
/// instance id, the workflow middleware puts it on the ambient during execution,
/// and this decorator propagates it to each child dispatched from within that
/// execution.
///
/// <para>An explicitly-set correlation is NEVER overridden (<c>??=</c>), so a
/// caller that wants a distinct correlation keeps it. Outside a workflow
/// execution the ambient is null and the request is passed through unchanged.</para>
///
/// <para><b>INVARIANT for anyone adding a definition-dispatch (review finding 4).</b>
/// Propagation is deliberately "broaden to all": every child dispatched from
/// within a run inherits that run's correlation, and a correlation-standing grant
/// covers EVERY ask on its correlation without being consumed. That is correct
/// only while every definition-dispatched child is genuinely part of the run. A
/// fire-and-forget child (notification, analytics, cleanup) that performs a gated
/// effect and is dispatched from inside a GRANTED run would ride the parent's
/// standing grant — set that child's <c>CorrelationId</c> to a distinct value at
/// the dispatch site so it does not. Today no such child performs a gated effect,
/// so this is a guard on future code, not a live hole.</para>
/// </summary>
public sealed class CorrelationPropagatingWorkflowDispatcher : IWorkflowDispatcher
{
    private readonly IWorkflowDispatcher _inner;

    public CorrelationPropagatingWorkflowDispatcher(IWorkflowDispatcher inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public Task<DispatchWorkflowResponse> DispatchAsync(
        DispatchWorkflowDefinitionRequest request,
        DispatchWorkflowOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            var ambient = RunCorrelation.Current;
            if (!string.IsNullOrWhiteSpace(ambient))
            {
                request.CorrelationId = ambient;
            }
        }
        return _inner.DispatchAsync(request, options, cancellationToken);
    }

    public Task<DispatchWorkflowResponse> DispatchAsync(
        DispatchWorkflowInstanceRequest request,
        DispatchWorkflowOptions options,
        CancellationToken cancellationToken = default)
        => _inner.DispatchAsync(request, options, cancellationToken);

    public Task<DispatchWorkflowResponse> DispatchAsync(
        DispatchTriggerWorkflowsRequest request,
        DispatchWorkflowOptions options,
        CancellationToken cancellationToken = default)
        => _inner.DispatchAsync(request, options, cancellationToken);

    public Task<DispatchWorkflowResponse> DispatchAsync(
        DispatchResumeWorkflowsRequest request,
        DispatchWorkflowOptions options,
        CancellationToken cancellationToken = default)
        => _inner.DispatchAsync(request, options, cancellationToken);
}
