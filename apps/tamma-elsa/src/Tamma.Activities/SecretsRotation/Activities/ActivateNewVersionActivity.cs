using Elsa.Workflows;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Activities.SecretsRotation.Activities;

/// <summary>
/// Story 29-6 AC2 step 5 — atomically flip the new version
/// <c>Pending → Active</c> and the previous active (if any)
/// <c>Active → RetiredGrace</c>. Emits <c>SWITCHED</c> + <c>ACTIVATED</c>
/// for dashboards.
/// </summary>
public class ActivateNewVersionActivity : RotationActivityBase
{
    public override string StepName => "activate";

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var state = GetState(context);
        var gateway = ResolveGateway(context);

        await gateway.ActivateVersionAsync(
                state.SecretId,
                state.NewVersionNumber,
                state.PreviousVersionNumber,
                context.CancellationToken)
            .ConfigureAwait(false);

        state.Activated = true;

        await EmitAsync(
            context,
            RotationAuditEvents.Switched,
            versionNumber: state.NewVersionNumber,
            data: new Dictionary<string, object?>
            {
                ["previousVersion"] = state.PreviousVersionNumber,
            }).ConfigureAwait(false);

        await EmitAsync(
            context,
            RotationAuditEvents.Activated,
            versionNumber: state.NewVersionNumber,
            data: new Dictionary<string, object?>
            {
                ["previousVersion"] = state.PreviousVersionNumber,
            }).ConfigureAwait(false);
    }
}
