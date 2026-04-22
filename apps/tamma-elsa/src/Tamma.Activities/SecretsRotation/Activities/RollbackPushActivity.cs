using Elsa.Workflows;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Activities.SecretsRotation.Activities;

/// <summary>
/// Story 29-6 AC2 — compensation for step 3. Invokes the handler's
/// <see cref="IRotationHandler.RollbackAsync"/> with the same
/// plaintext / context the push used so the downstream system is
/// returned to its pre-rotation state.
///
/// <para>If rollback itself throws the activity emits
/// <c>SECRET.ROTATION.COMPENSATION.FAILED</c> and rethrows — operator
/// intervention is required (Story 29-6 AC6).</para>
/// </summary>
public class RollbackPushActivity : RotationActivityBase
{
    public override string StepName => "rollback-push";

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var state = GetState(context);
        if (!state.Pushed)
            return; // nothing to roll back

        var registry = ResolveRegistry(context);
        var handler = registry.Resolve(state.HandlerSystem)
            ?? throw new InvalidOperationException(
                $"Handler '{state.HandlerSystem}' missing at rollback.");

        var rotationContext = BuildRotationContext(state);
        var target = BuildTarget(state);

        await EmitAsync(
            context,
            RotationAuditEvents.CompensationStarted,
            versionNumber: state.NewVersionNumber,
            detail: state.Error ?? "rollback").ConfigureAwait(false);

        try
        {
            await handler.RollbackAsync(
                    target,
                    state.NewPlaintext,
                    rotationContext,
                    context.CancellationToken)
                .ConfigureAwait(false);

            await EmitAsync(
                context,
                RotationAuditEvents.CompensationSuccess,
                versionNumber: state.NewVersionNumber).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await EmitAsync(
                context,
                RotationAuditEvents.CompensationFailed,
                versionNumber: state.NewVersionNumber,
                detail: ex.GetType().Name,
                data: new Dictionary<string, object?>
                {
                    ["message"] = ex.Message,
                }).ConfigureAwait(false);
            throw;
        }
    }
}
