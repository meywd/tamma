namespace Tamma.Data.Entities;

/// <summary>
/// Story 41-30 (D2, Correction 3) — the durable at-most-once fire ledger
/// (<c>scheduled_trigger_fires</c>). One row per attempted
/// <c>(trigger, window)</c>; <c>UNIQUE (TriggerId, WindowKey)</c> IS the
/// dedupe: the claim is <c>INSERT … ON CONFLICT DO NOTHING</c>, 1 row = we
/// own this window, 0 = someone already did it. Correct across pods,
/// restarts and clock skew because Postgres arbitrates it.
///
/// <para><b>Why the advisory lock alone is not enough:</b>
/// <c>pg_try_advisory_lock</c> is SESSION-scoped — released the instant the
/// connection closes, including by a pod crash. The lock prevents
/// CONCURRENT double-fire; only this committed ledger row prevents
/// SEQUENTIAL double-fire after a crash. Both mechanisms ship (D4).</para>
///
/// <para>The contract is <b>at-most-once per window</b> (Correction 4):
/// a crash between claim-commit and dispatch LOSES the fire — surfaced as
/// a <c>claimed</c> row that never became <c>dispatched</c> plus
/// <c>SCHEDULE.FIRE.FAILED</c> where detectable — and the next window is
/// the recovery path, never a silent retry of the same window.</para>
///
/// <para>Rows older than the retention window (default 90 days) are pruned
/// on the tick so the ledger does not grow without bound. Excluded from the
/// destructive startup DROP list alongside <see cref="ScheduledTrigger"/>
/// (AC7); no FK to <c>tenants</c> for the same reason.</para>
/// </summary>
public class ScheduledTriggerFire
{
    public Guid Id { get; set; }

    /// <summary>FK to <see cref="ScheduledTrigger"/> (cascade — the ledger dies with its trigger).</summary>
    public Guid TriggerId { get; set; }

    /// <summary>The concrete tenant fired for (fires are always against materialised rows).</summary>
    public Guid TenantId { get; set; }

    /// <summary>Denormalised definition id at claim time (audit convenience).</summary>
    public string DefinitionId { get; set; } = null!;

    /// <summary>
    /// Opaque window key — the ISO-8601 UTC instant of the window's scheduled
    /// fire time (e.g. <c>2026-07-27T03:00:00Z</c>), or <c>manual:{timestamp}</c>
    /// for an admin run-now claim (D8). The seam never parses it back.
    /// </summary>
    public string WindowKey { get; set; } = null!;

    public DateTime ClaimedAt { get; set; }

    /// <summary>
    /// For cron fires: the successful dispatch instant. For manual run-now
    /// fires it is ALSO the drain's CAS marker (stamped the moment a pod wins
    /// the dispatch attempt, while <see cref="Outcome"/> is still
    /// <c>claimed</c> — MAJOR-1 fix, 2026-07-29). A <c>claimed</c> row with a
    /// non-null <c>DispatchedAt</c> is therefore a BURNT manual fire (the pod
    /// crashed or the outcome stamp failed mid-dispatch) — it is never
    /// re-drained; at-most-once wins over delivery.
    /// </summary>
    public DateTime? DispatchedAt { get; set; }

    public string? WorkflowInstanceId { get; set; }

    /// <summary>Closed set: <c>claimed</c> | <c>dispatched</c> | <c>failed</c> (CHECK-pinned).</summary>
    public string Outcome { get; set; } = "claimed";

    public string? Detail { get; set; }
}
