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
///   <item><see cref="FireSuppressed"/> — another pod won the window (ledger
///     claim returned 0 rows, or the advisory lock was held). INFO, not an
///     error — suppression is the at-most-once contract WORKING.</item>
///   <item><see cref="WindowSkipped"/> — LOUD: bounded catch-up (D7) dropped
///     missed windows; carries <c>skippedCount</c> + the first/last skipped
///     <c>windowKey</c> so the gap is auditable rather than invisible.</item>
///   <item><see cref="FireFailed"/> — LOUD: the dispatch threw. The ledger
///     row is stamped <c>failed</c> and the NEXT window is the recovery path
///     (at-most-once per window, Correction 4 — never a silent same-window
///     retry).</item>
///   <item><see cref="TriggerChanged"/> — an admin created / updated /
///     deleted / ran a schedule (emitted by the admin API).</item>
/// </list></para>
/// </summary>
public static class ScheduleEvents
{
    public const string FireDispatched = "SCHEDULE.FIRE.DISPATCHED";
    public const string FireSuppressed = "SCHEDULE.FIRE.SUPPRESSED";
    public const string WindowSkipped = "SCHEDULE.WINDOW.SKIPPED";
    public const string FireFailed = "SCHEDULE.FIRE.FAILED";
    public const string TriggerChanged = "SCHEDULE.TRIGGER.CHANGED";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through inputs.
    /// Returns <c>null</c> for empty / unparseable values (mirrors
    /// <see cref="Documents.ApprovalEvents.ParseTenantId"/>).
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: <see cref="WindowSkipped"/> and
    /// <see cref="FireFailed"/> are LOUD (error-status) rows — a dropped
    /// window and a failed dispatch are the two things an audit trail exists
    /// to make visible. Everything else (<see cref="FireDispatched"/>,
    /// <see cref="FireSuppressed"/>, <see cref="TriggerChanged"/>) is a
    /// normal success row — suppression in particular is the at-most-once
    /// contract doing its job, not a failure.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        WindowSkipped => "error",
        FireFailed => "error",
        _ => "success",
    };
}
