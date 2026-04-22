using Elsa.Extensions;
using Elsa.Workflows;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Activities.SecretsRotation.Activities;

/// <summary>
/// Story 29-6 — base for every rotation-saga step activity.
/// Provides shared helpers:
///
/// <list type="bullet">
///   <item><description>Resolving the <see cref="ISecretRotationGateway"/>
///     / <see cref="IRotationAuditEmitter"/> from DI via the activity
///     execution context.</description></item>
///   <item><description>Emitting a per-step audit event with the
///     rotation correlation id so dashboards can join all events for
///     the same saga.</description></item>
///   <item><description>Emitting start/success/failure
///     <c>tamma:events</c> entries via the base
///     <see cref="TammaAsyncActivity"/> machinery so existing in-process
///     observers keep working.</description></item>
/// </list>
/// </summary>
public abstract class RotationActivityBase : TammaAsyncActivity
{
    /// <summary>Short kebab-case step identifier (<c>mint-pending</c>, <c>push-new</c>, ...).</summary>
    public abstract string StepName { get; }

    public override string? EventType =>
        $"SECRET.ROTATION.{StepName.ToUpperInvariant().Replace('-', '_')}";

    protected ISecretRotationGateway ResolveGateway(ActivityExecutionContext ctx) =>
        ctx.GetRequiredService<ISecretRotationGateway>();

    protected IRotationAuditEmitter ResolveAuditor(ActivityExecutionContext ctx) =>
        ctx.GetRequiredService<IRotationAuditEmitter>();

    protected IRotationHandlerRegistry ResolveRegistry(ActivityExecutionContext ctx) =>
        ctx.GetRequiredService<IRotationHandlerRegistry>();

    protected IRetireScheduler ResolveRetireScheduler(ActivityExecutionContext ctx) =>
        ctx.GetRequiredService<IRetireScheduler>();

    protected ILogger<RotationActivityBase>? ResolveLogger(ActivityExecutionContext ctx) =>
        ctx.GetService<ILogger<RotationActivityBase>>();

    protected static RotationWorkflowState GetState(ActivityExecutionContext ctx) =>
        GetStateStatic(ctx);

    /// <summary>Accessible to non-subclasses (e.g. SagaRunner).</summary>
    internal static RotationWorkflowState GetStateStatic(ActivityExecutionContext ctx)
    {
        if (!ctx.WorkflowExecutionContext.TransientProperties.TryGetValue("rotation:state", out var raw)
            || raw is not RotationWorkflowState state)
        {
            state = new RotationWorkflowState();
            ctx.WorkflowExecutionContext.TransientProperties["rotation:state"] = state;
        }
        return state;
    }

    protected RotationContext BuildRotationContext(RotationWorkflowState state, bool dryRun = false) =>
        new(
            state.RotationCorrelationId,
            state.OperatorUserId,
            dryRun,
            state.HandlerOptions);

    protected RotationTarget BuildTarget(RotationWorkflowState state)
    {
        if (state.Snapshot is null)
            throw new InvalidOperationException(
                $"Rotation state has no snapshot yet at step '{StepName}'. " +
                "MintPendingVersionActivity must populate it first.");

        return new RotationTarget(
            state.Snapshot.SecretId,
            state.Snapshot.Name,
            state.Snapshot.TenantId,
            state.Snapshot.ConsumerSystem,
            state.Snapshot.ConsumerIdentifier,
            state.NewVersionNumber,
            state.PreviousVersionNumber);
    }

    protected async Task EmitAsync(
        ActivityExecutionContext ctx,
        string eventType,
        int? versionNumber = null,
        string? detail = null,
        IReadOnlyDictionary<string, object?>? data = null)
    {
        var state = GetState(ctx);
        var auditor = ResolveAuditor(ctx);
        await auditor.EmitAsync(
            RotationAuditEvent.Create(
                eventType,
                state.SecretId,
                state.Snapshot?.TenantId,
                state.RotationCorrelationId,
                versionNumber,
                detail,
                data),
            ctx.CancellationToken);
    }
}
