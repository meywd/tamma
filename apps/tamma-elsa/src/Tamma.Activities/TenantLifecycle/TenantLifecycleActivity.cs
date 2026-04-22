using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Base for the eleven create-tenant + ten delete-tenant activities. Each
/// concrete activity overrides <see cref="StepName"/> +
/// <see cref="ProcessAsync"/>. The base handles the common surface:
///
/// <list type="bullet">
///   <item><description>Reads <c>TenantId</c> input.</description></item>
///   <item><description>Emits the <c>TENANT.PROVISION.STEP_STARTED</c> event
///     before the work, <c>STEP_COMPLETED</c> on success,
///     <c>STEP_FAILED</c> on exception. All three carry <c>step</c> +
///     <c>attempt</c> tags so the step-dedup index from Story 28-6
///     swallows replays.</description></item>
///   <item><description>Resolves the <see cref="IPlatformEventPublisher"/>
///     from the activity execution scope so the activity does not need
///     a constructor (Elsa code-first activities are <c>new()</c>'d at
///     workflow build time, before DI exists).</description></item>
///   <item><description>Inherits the <c>tamma:events</c> in-process event
///     emission from <see cref="TammaAsyncActivity"/> so the existing
///     telemetry sink continues to work alongside the durable
///     platform-event emission added here.</description></item>
/// </list>
///
/// <para>The <see cref="ProcessAsync"/> contract is "succeed or throw" —
/// per Doc 03 §5.3, retryability is decided by the workflow / Elsa retry
/// policy, not by this base. Idempotency is the activity's
/// responsibility.</para>
/// </summary>
public abstract class TenantLifecycleActivity : TammaAsyncActivity
{
    [Input(Description = "Tenant id this lifecycle step targets.")]
    public Input<Guid> TenantId { get; set; } = default!;

    [Input(
        Description = "Retry attempt number. Starts at 1. Used as a tag on the "
                      + "STEP_* events so the partial-unique step-dedup index "
                      + "from Story 28-6 swallows true replays.")]
    public Input<int> Attempt { get; set; } = new(1);

    /// <summary>Per Doc 03 §2.1 — short kebab-case step identifier
    /// (<c>create-role</c>, <c>migrate-tenant-db</c>, ...).</summary>
    public abstract string StepName { get; }

    /// <summary>Whether the activity emits the <c>STEP_*</c> events. The
    /// few activities that already emit a richer terminal event
    /// (<see cref="MarkActiveActivity"/>, <see cref="EmitDeletedSuccessActivity"/>)
    /// override this to <c>false</c>.</summary>
    protected virtual bool EmitStepEvents => true;

    public override string? EventType => $"TENANT.LIFECYCLE.{StepName.ToUpperInvariant().Replace('-', '_')}";

    protected sealed override async Task RunAsync(ActivityExecutionContext context)
    {
        var tenantId = TenantId.Get(context);
        var attempt = SafeAttempt(context);
        var publisher = context.GetRequiredService<IPlatformEventPublisher>();
        Logger ??= context.GetService<ILogger<TenantLifecycleActivity>>();

        if (EmitStepEvents)
        {
            await publisher.AppendAndPublishAsync(
                TenantLifecycleEvents.BuildEvent(
                    TenantLifecycleEvents.ProvisionStepStarted,
                    tenantId,
                    step: StepName,
                    attempt: attempt),
                context.CancellationToken).ConfigureAwait(false);
        }

        try
        {
            await ProcessAsync(context, tenantId, attempt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (EmitStepEvents)
            {
                try
                {
                    await publisher.AppendAndPublishAsync(
                        TenantLifecycleEvents.BuildEvent(
                            TenantLifecycleEvents.ProvisionStepFailed,
                            tenantId,
                            step: StepName,
                            attempt: attempt,
                            data: new Dictionary<string, object?>
                            {
                                ["errorType"] = ex.GetType().Name,
                                // Caller-controlled message — DO NOT log
                                // tenant connection strings, passwords,
                                // raw SQL with secrets in here. The
                                // activity must scrub before throwing if
                                // the message is sensitive.
                                ["message"] = ex.Message,
                            }),
                        context.CancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Swallow — emitting the failure event is best-effort,
                    // the underlying ProcessAsync exception is what the
                    // workflow needs to see.
                }
            }
            throw;
        }

        if (EmitStepEvents)
        {
            await publisher.AppendAndPublishAsync(
                TenantLifecycleEvents.BuildEvent(
                    TenantLifecycleEvents.ProvisionStepCompleted,
                    tenantId,
                    step: StepName,
                    attempt: attempt),
                context.CancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Concrete activities implement this. <paramref name="tenantId"/> +
    /// <paramref name="attempt"/> are pre-resolved from inputs to keep the
    /// happy-path body terse.
    /// </summary>
    protected abstract Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt);

    private int SafeAttempt(ActivityExecutionContext context)
    {
        // Allow the workflow to omit the input on the first attempt and
        // fall through to "1". Elsa returns the default(int) (= 0) when
        // the input was never bound by the workflow caller; clamp up.
        var raw = Attempt.Get(context);
        return raw <= 0 ? 1 : raw;
    }
}
