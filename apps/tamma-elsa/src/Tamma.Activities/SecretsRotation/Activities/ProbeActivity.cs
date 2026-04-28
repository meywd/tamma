using Elsa.Workflows;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Activities.SecretsRotation.Activities;

/// <summary>
/// Story 29-6 AC2 step 4 — verify the push landed by calling
/// <see cref="IRotationHandler.ProbeAsync"/> with 3× retry per the
/// brief. Success emits <c>PROBE.SUCCESS</c>; three consecutive
/// unhealthy results emit <c>PROBE.FAILED</c> and throw so the
/// workflow drops into compensation.
/// </summary>
public class ProbeActivity : RotationActivityBase
{
    public override string StepName => "probe";

    /// <summary>Override to shrink the backoff in tests.</summary>
    public IReadOnlyList<TimeSpan> ProbeDelays { get; set; } = new[]
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
                $"Handler '{state.HandlerSystem}' unexpectedly missing at probe step.");

        var rotationContext = BuildRotationContext(state);
        var target = BuildTarget(state);

        ProbeResult? last = null;
        for (var attempt = 0; attempt <= ProbeDelays.Count; attempt++)
        {
            if (attempt > 0)
            {
                var delay = ProbeDelays[attempt - 1];
                try
                {
                    await Task.Delay(delay, context.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
            }

            last = await handler.ProbeAsync(target, rotationContext, context.CancellationToken)
                .ConfigureAwait(false);
            if (last.IsHealthy)
            {
                await EmitAsync(
                    context,
                    RotationAuditEvents.ProbeSuccess,
                    versionNumber: state.NewVersionNumber,
                    data: new Dictionary<string, object?>
                    {
                        ["attempt"] = attempt + 1,
                        ["durationMs"] = last.DurationMs,
                    }).ConfigureAwait(false);
                return;
            }
        }

        await EmitAsync(
            context,
            RotationAuditEvents.ProbeFailed,
            versionNumber: state.NewVersionNumber,
            detail: last?.Reason ?? "unknown",
            data: new Dictionary<string, object?>
            {
                ["attempts"] = ProbeDelays.Count + 1,
                ["durationMs"] = last?.DurationMs ?? 0,
            }).ConfigureAwait(false);

        state.Error = $"probe_failed:{last?.Reason}";
        throw new InvalidOperationException(
            $"Probe failed after {ProbeDelays.Count + 1} attempts. Reason: {last?.Reason}");
    }
}
