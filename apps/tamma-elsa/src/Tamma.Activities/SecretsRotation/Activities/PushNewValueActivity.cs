using Elsa.Workflows;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Activities.SecretsRotation.Activities;

/// <summary>
/// Story 29-6 AC2 step 3 — invoke the handler's
/// <see cref="IRotationHandler.PushAsync"/> with 3× exponential-
/// backoff retries (5s / 15s / 45s per brief AC6). Success emits
/// <c>SECRET.ROTATION.PUSH.SUCCESS</c>; final failure emits
/// <c>PUSH.FAILED</c> and rethrows so the workflow enters
/// compensation.
///
/// <para>The handler itself must be idempotent on
/// <see cref="RotationContext.RotationCorrelationId"/>; this activity's
/// retry is only for transient network-class failures.</para>
/// </summary>
public class PushNewValueActivity : RotationActivityBase
{
    public override string StepName => "push-new";

    /// <summary>Override to shrink the backoff in tests.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } = new[]
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
    };

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var state = GetState(context);
        var registry = ResolveRegistry(context);
        var handler = registry.Resolve(state.HandlerSystem)
            ?? throw new InvalidOperationException(
                $"Handler '{state.HandlerSystem}' unexpectedly missing at push step.");

        var rotationContext = BuildRotationContext(state);
        var target = BuildTarget(state);

        Exception? lastError = null;
        for (var attempt = 0; attempt <= RetryDelays.Count; attempt++)
        {
            try
            {
                await handler.PushAsync(
                        target,
                        state.NewPlaintext,
                        rotationContext,
                        context.CancellationToken)
                    .ConfigureAwait(false);

                state.Pushed = true;
                await EmitAsync(
                    context,
                    RotationAuditEvents.PushSuccess,
                    versionNumber: state.NewVersionNumber,
                    data: new Dictionary<string, object?> { ["attempt"] = attempt + 1 })
                    .ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt >= RetryDelays.Count)
                    break;

                var delay = RetryDelays[attempt];
                try
                {
                    await Task.Delay(delay, context.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
            }
        }

        await EmitAsync(
            context,
            RotationAuditEvents.PushFailed,
            versionNumber: state.NewVersionNumber,
            detail: lastError?.GetType().Name ?? "unknown",
            data: new Dictionary<string, object?>
            {
                ["message"] = Truncate(lastError?.Message, 240),
                ["attempts"] = RetryDelays.Count + 1,
            }).ConfigureAwait(false);

        state.Error = $"push_failed:{lastError?.GetType().Name}";
        throw lastError ?? new InvalidOperationException("PushAsync failed for unknown reasons.");
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..max];
    }
}
