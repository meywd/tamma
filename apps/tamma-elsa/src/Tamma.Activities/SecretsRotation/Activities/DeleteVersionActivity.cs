using Elsa.Workflows;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Activities.SecretsRotation.Activities;

/// <summary>
/// Story 29-6 AC2 — compensation for step 1. Hard-deletes the pending
/// version row when the rotation aborts before activation. Idempotent —
/// if the row is already absent the gateway swallows the delete.
/// </summary>
public class DeleteVersionActivity : RotationActivityBase
{
    public override string StepName => "delete-version";

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var state = GetState(context);
        if (state.NewVersionNumber <= 0)
            return; // nothing minted yet

        var gateway = ResolveGateway(context);
        await gateway.DeleteVersionAsync(
                state.SecretId,
                state.NewVersionNumber,
                context.CancellationToken)
            .ConfigureAwait(false);
    }
}
