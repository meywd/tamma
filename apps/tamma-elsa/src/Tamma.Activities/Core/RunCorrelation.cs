using Elsa.Workflows;
using Elsa.Workflows.Pipelines.WorkflowExecution;

namespace Tamma.Activities.Core;

/// <summary>
/// Story 43-14 (Amendment 2-B) — the RUN correlation, an ambient
/// <see cref="AsyncLocal{T}"/> that carries the cycle's correlation through the
/// whole run so one human approval's correlation-standing grant is what every
/// mediated call in that run presents to Seam C.
///
/// <para>Seeded once per workflow execution by
/// <see cref="RunCorrelationWorkflowMiddleware"/> to
/// <c>WorkflowExecutionContext.CorrelationId ?? WorkflowExecutionContext.Id</c>.
/// <c>DispatchCycleActivity</c> sets the cycle's correlation to the cycle
/// instance id, and <see cref="CorrelationPropagatingWorkflowDispatcher"/>
/// propagates it to every dispatched sub-workflow — so a whole chain shares one
/// ledger-visible correlation. <c>TammaApiClient</c> reads
/// <see cref="Current"/> to stamp <c>X-Tamma-Correlation-Id</c> on its mediation
/// calls.</para>
///
/// <para>Outside a workflow execution (an ordinary Tamma.Api host request), the
/// ambient is null and the header is absent, so Seam C's <c>route:</c>-derived
/// fallback stands unchanged.</para>
/// </summary>
public static class RunCorrelation
{
    private static readonly AsyncLocal<string?> _current = new();

    /// <summary>The current run's correlation, or null outside a workflow run.</summary>
    public static string? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <summary>
    /// Set the ambient for the duration of <paramref name="body"/>, restoring the
    /// previous value on exit (so nested workflow executions on the same async
    /// flow do not clobber an outer run's correlation).
    /// </summary>
    public static async ValueTask WithAsync(string? correlationId, Func<ValueTask> body)
    {
        var previous = _current.Value;
        _current.Value = correlationId;
        try
        {
            await body().ConfigureAwait(false);
        }
        finally
        {
            _current.Value = previous;
        }
    }
}

/// <summary>
/// Workflow-execution-pipeline middleware that seeds <see cref="RunCorrelation.Current"/>
/// for the duration of every workflow execution. Modelled on
/// <see cref="EventPersistenceWorkflowMiddleware"/>; registered the
/// <c>UseTammaEventPersistence</c> way (never via
/// <c>ConfigureDefaultActivityExecutionPipeline</c> — see that class's doc for the
/// silent-no-op trap).
/// </summary>
public class RunCorrelationWorkflowMiddleware(WorkflowMiddlewareDelegate next)
    : WorkflowExecutionMiddleware(next)
{
    public override async ValueTask InvokeAsync(WorkflowExecutionContext context)
    {
        var correlation = string.IsNullOrWhiteSpace(context.CorrelationId)
            ? context.Id
            : context.CorrelationId;

        await RunCorrelation.WithAsync(correlation, async () =>
            await Next(context).ConfigureAwait(false)).ConfigureAwait(false);
    }
}
