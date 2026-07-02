namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Story 30-2 (Phase-B I1 restructure) — the three ways a single
/// invocation of <see cref="ProvisionTenantV2Workflow.ExecuteAsync"/> can
/// end.
///
/// <para>Before this restructure the workflow's InitialProbe step
/// block-polled the provider in an in-process <c>while</c> loop (up to the
/// ~30-min probe budget), which pinned the one-task-at-a-time
/// <c>PlatformTaskWorker</c> slot for the whole poll. On a single worker
/// process the outer saga occupied the only slot, so the inner
/// <c>provisioning.tenant</c> task the Cranl provider enqueues on the SAME
/// queue was never reserved and the provision timed out. The fix makes the
/// probe SINGLE-SHOT: one <c>GetStatusAsync</c> per invocation, then one of
/// these outcomes. On <see cref="DeferRequested"/> the handler releases the
/// worker slot (defer + <c>PlatformTaskDeferredException</c>) so the single
/// worker can interleave the inner task before re-entering the saga.</para>
/// </summary>
public enum ProvisionTenantV2OutcomeKind
{
    /// <summary>Terminal — the tenant reached <c>Ready</c> and was activated.</summary>
    Completed,

    /// <summary>Terminal — the workflow stamped <c>Failed</c> (provider
    /// failure, probe budget exceeded, ...) and ran any compensations.</summary>
    Failed,

    /// <summary>Non-terminal — the tenant is still provisioning and the probe
    /// budget has NOT been exceeded. The handler must return the task to the
    /// queue with a future <c>VisibleAt</c> (defer by
    /// <see cref="ProvisionTenantV2Outcome.DeferDelay"/>) so the worker slot
    /// is released and the saga re-enters after the interval.</summary>
    DeferRequested,
}

/// <summary>
/// Result of one <see cref="ProvisionTenantV2Workflow.ExecuteAsync"/>
/// invocation. Wraps the pre-existing <see cref="ProvisioningResult"/> so
/// callers/tests can keep reading <see cref="Status"/> exactly as before,
/// while adding the <see cref="Kind"/> discriminator + the
/// <see cref="DeferDelay"/> the handler needs to schedule the next probe.
/// </summary>
public sealed class ProvisionTenantV2Outcome
{
    private ProvisionTenantV2Outcome(
        ProvisionTenantV2OutcomeKind kind,
        ProvisioningResult result,
        TimeSpan deferDelay)
    {
        Kind = kind;
        Result = result;
        DeferDelay = deferDelay;
    }

    /// <summary>Which of the three terminal/non-terminal outcomes occurred.</summary>
    public ProvisionTenantV2OutcomeKind Kind { get; }

    /// <summary>The underlying provisioning snapshot. For
    /// <see cref="ProvisionTenantV2OutcomeKind.DeferRequested"/> this carries
    /// the last (non-terminal) snapshot the probe observed.</summary>
    public ProvisioningResult Result { get; }

    /// <summary>How long the handler should defer the task before the next
    /// probe. Non-zero only for
    /// <see cref="ProvisionTenantV2OutcomeKind.DeferRequested"/> (equals the
    /// workflow's <c>ProbeInterval</c>).</summary>
    public TimeSpan DeferDelay { get; }

    /// <summary>Convenience pass-through so existing assertions
    /// (<c>outcome.Status.State</c> / <c>outcome.Status.FailureReason</c>)
    /// keep compiling unchanged.</summary>
    public ProvisioningStatusSnapshot Status => Result.Status;

    /// <summary><c>true</c> when the handler must defer + release the slot.</summary>
    public bool IsDeferRequested => Kind == ProvisionTenantV2OutcomeKind.DeferRequested;

    public static ProvisionTenantV2Outcome Completed(ProvisioningResult result) =>
        new(ProvisionTenantV2OutcomeKind.Completed, result, TimeSpan.Zero);

    public static ProvisionTenantV2Outcome Failed(ProvisioningResult result) =>
        new(ProvisionTenantV2OutcomeKind.Failed, result, TimeSpan.Zero);

    public static ProvisionTenantV2Outcome Defer(
        TimeSpan deferDelay, ProvisioningStatusSnapshot lastSnapshot) =>
        new(
            ProvisionTenantV2OutcomeKind.DeferRequested,
            new ProvisioningResult(lastSnapshot, new Dictionary<string, string>()),
            deferDelay);
}
