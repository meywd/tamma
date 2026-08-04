namespace Tamma.Activities.Scheduling;

/// <summary>
/// Story 41-30 — central catalogue of the <c>SCHEDULE.*</c> event family
/// emitted by the tenant-aware scheduled-trigger seam
/// (<c>TenantScheduledTriggerService</c> + the admin API). Type pattern
/// follows the platform's <c>AGGREGATE.ACTION.STATUS</c> convention and
/// mirrors the sibling catalogues (<see cref="Documents.ApprovalEvents"/>,
/// <see cref="Documents.ChannelEvents"/>).
///
/// <para><b>Emit sites:</b>
/// <list type="bullet">
///   <item><see cref="FireDispatched"/> — the tick claimed the ledger row and
///     the dispatch succeeded. Tags: <c>tenantId</c>, <c>definitionId</c>,
///     <c>windowKey</c>, <c>triggerId</c>.</item>
///   <item><see cref="FireSuppressed"/> — a CONCURRENT pod won the window
///     (the advisory lock was held, or the ledger claim lost the race to a
///     still-in-flight claim). INFO, not an error — suppression is the
///     at-most-once contract WORKING. <b>Not</b> emitted for a window that
///     already reached a terminal ledger state: re-observing a settled window
///     on a later tick is silent (MODERATE-5 fix, 2026-07-30).</item>
///   <item><see cref="WindowSkipped"/> — LOUD: bounded catch-up (D7) dropped
///     missed windows; carries <c>skippedCount</c> + the first/last skipped
///     <c>windowKey</c> so the gap is auditable rather than invisible.
///     <b>Emitted only after the ledger claim for the firing window is WON</b>
///     (MODERATE-5 fix) — the claim is the at-most-once arbiter, so a given
///     <c>(trigger, window)</c> emits this at most once fleet-wide.
///     <c>skippedCount</c> counts every window since the last SUCCESSFUL
///     dispatch that this trigger did not run — which includes any window that
///     was attempted and BURNT (each of those has its own
///     <see cref="FireFailed"/> row); it is not a claim that they were never
///     tried.</item>
///   <item><see cref="FireFailed"/> — LOUD: the dispatch threw. The ledger
///     row is stamped <c>failed</c> and the NEXT window is the recovery path
///     (at-most-once per window, Correction 4 — never a silent same-window
///     retry).</item>
///   <item><see cref="FireAbandoned"/> — LOUD (LOW-8 fix, 2026-07-30): the
///     stale-claim sweep found a ledger row still <c>claimed</c> long after
///     <c>ClaimedAt</c> — the pod that won the claim died before it could
///     stamp an outcome. The sweep stamps the row <c>failed</c> (the window is
///     burnt — at-most-once forbids a retry) and emits this so the lost fire
///     has a surface other than manual SQL. Distinct from
///     <see cref="FireFailed"/> precisely so "the dispatch was attempted and
///     threw" and "the fire vanished with its pod" are separable in the event
///     stream.</item>
///   <item><see cref="TriggerChanged"/> — an admin created / updated /
///     deleted / ran a schedule (emitted by the admin API).</item>
/// </list></para>
///
/// <para><b>Reading the stream (MODERATE-5, 2026-07-30).</b> There is no
/// same-window retry anywhere in this seam, and every emission below is
/// gated on a state transition that happens at most once per
/// <c>(trigger, window)</c>. So a window contributes AT MOST ONE terminal row
/// — <see cref="FireDispatched"/>, <see cref="FireFailed"/> or
/// <see cref="FireAbandoned"/> — plus at most one <see cref="WindowSkipped"/>.
/// Repeated rows for one <c>windowKey</c> therefore mean a genuine
/// concurrency event, never a retry loop: a run of
/// <see cref="FireSuppressed"/> rows is bounded by how long a claim stays
/// in-flight (and, for an abandoned claim, by the sweep threshold).</para>
/// </summary>
public static class ScheduleEvents
{
    public const string FireDispatched = "SCHEDULE.FIRE.DISPATCHED";
    public const string FireSuppressed = "SCHEDULE.FIRE.SUPPRESSED";
    public const string WindowSkipped = "SCHEDULE.WINDOW.SKIPPED";
    public const string FireFailed = "SCHEDULE.FIRE.FAILED";

    /// <summary>
    /// LOW-8 fix (2026-07-30) — a ledger row left <c>claimed</c> by a pod that
    /// died between the claim and the outcome stamp, found by the bounded
    /// stale-claim sweep and burnt. See the class doc.
    /// </summary>
    public const string FireAbandoned = "SCHEDULE.FIRE.ABANDONED";

    public const string TriggerChanged = "SCHEDULE.TRIGGER.CHANGED";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through inputs.
    /// Returns <c>null</c> for empty / unparseable values (mirrors
    /// <see cref="Documents.ApprovalEvents.ParseTenantId"/>).
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: <see cref="WindowSkipped"/>,
    /// <see cref="FireFailed"/> and <see cref="FireAbandoned"/> are LOUD
    /// (error-status) rows — a dropped window, a failed dispatch and a fire
    /// lost with its pod are the three things an audit trail exists to make
    /// visible. Everything else (<see cref="FireDispatched"/>,
    /// <see cref="FireSuppressed"/>, <see cref="TriggerChanged"/>) is a
    /// normal success row — suppression in particular is the at-most-once
    /// contract doing its job, not a failure.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        WindowSkipped => "error",
        FireFailed => "error",
        FireAbandoned => "error",
        _ => "success",
    };
}
