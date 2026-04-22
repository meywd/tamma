using Elsa.Workflows;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Activities.SecretsRotation.Activities;

/// <summary>
/// Story 29-6 AC2 step 2 — resolve the
/// <see cref="IRotationHandler"/> that matches the secret's first
/// consumer-ref system. Fails fast when no handler is registered so
/// the workflow short-circuits to the compensation path before
/// pushing any state.
/// </summary>
public class ResolveHandlerActivity : RotationActivityBase
{
    public override string StepName => "resolve-handler";

    protected override Task RunAsync(ActivityExecutionContext context)
    {
        var state = GetState(context);
        if (state.Snapshot is null)
            throw new InvalidOperationException(
                "MintPendingVersionActivity must populate the snapshot first.");

        var registry = ResolveRegistry(context);
        var system = state.Snapshot.ConsumerSystem;
        if (string.IsNullOrWhiteSpace(system))
            throw new InvalidOperationException(
                $"Secret '{state.SecretId}' has no consumer ref — " +
                "cannot resolve a rotation handler.");

        var handler = registry.Resolve(system)
            ?? registry.Resolve("generic-http");

        if (handler is null)
            throw new InvalidOperationException(
                $"No rotation handler registered for system '{system}' and " +
                "no 'generic-http' fallback handler is available.");

        state.HandlerSystem = handler.System;
        return Task.CompletedTask;
    }
}
