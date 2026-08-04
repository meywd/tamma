using Tamma.Data.Entities;

namespace Tamma.Data.Abstractions;

/// <summary>
/// Story 41-30 — persistence port for the tenant-aware scheduled-trigger
/// seam. Implemented by
/// <see cref="Repositories.ScheduledTriggerRepository"/> over the
/// control-plane tables <c>scheduled_triggers</c> +
/// <c>scheduled_trigger_fires</c>; the hosted service
/// (<c>TenantScheduledTriggerService</c>, Tamma.ElsaServer) resolves this
/// port per tick and tests inject a fake.
/// </summary>
public interface IScheduledTriggerRepository
{
    /// <summary>
    /// Snapshot of active (non-soft-deleted) tenant ids, ordered by id — the
    /// landed fan-out pattern from
    /// <c>QueuedTaskRepository.ListPendingFromAnyTenantAsync</c>.
    /// </summary>
    Task<IReadOnlyList<Guid>> SnapshotActiveTenantIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// D6 — materialise a concrete per-tenant row for every active tenant
    /// that lacks one for each enabled template's
    /// <c>(DefinitionId, Name)</c>. Idempotent across pods (insert-missing
    /// via <c>ON CONFLICT DO NOTHING</c> against the natural-key index).
    /// Returns the number of rows created.
    /// </summary>
    Task<int> MaterialiseTemplatesAsync(
        IReadOnlyList<Guid> activeTenantIds, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Enabled, CONCRETE (tenant-scoped) triggers whose tenant is in
    /// <paramref name="activeTenantIds"/>. Templates are never returned —
    /// they are materialised, not fired (D6).
    /// </summary>
    Task<IReadOnlyList<ScheduledTrigger>> ListEnabledConcreteTriggersAsync(
        IReadOnlyList<Guid> activeTenantIds, CancellationToken ct = default);

    /// <summary>
    /// THE correctness core (D2): claim <c>(TriggerId, WindowKey)</c> in the
    /// fire ledger via <c>INSERT … ON CONFLICT DO NOTHING</c>.
    /// <c>true</c> = this caller owns the window; <c>false</c> = another pod
    /// (or an earlier run of this pod) already did — across pods, restarts
    /// and clock skew, because Postgres arbitrates the unique index.
    ///
    /// <para><b>The unit of at-most-once is the TRIGGER ROW, not the
    /// definition</b> (LOW-7, decided 2026-07-30 — option (b), keep the
    /// per-trigger key). A trigger row IS a schedule: its
    /// <c>(TenantId, DefinitionId, Name)</c> natural key exists precisely so
    /// one tenant can run the same definition on two different cadences with
    /// two different <c>InputJson</c> payloads. Folding <c>DefinitionId</c>
    /// into this key would make one of those schedules silently swallow the
    /// other whenever their windows coincide — dropping configured work, which
    /// is worse than the duplicate it prevents. Consumers get
    /// <c>triggerId</c> as a dispatch input and can scope their idempotency
    /// per schedule (<c>triggerId</c> + <c>windowKey</c>) or per definition
    /// (<c>tenantId</c> + <c>definitionId</c> + <c>windowKey</c>, treating the
    /// second call as a replay) — both discriminators are on the wire.</para>
    /// </summary>
    Task<bool> TryClaimFireAsync(ScheduledTriggerFire fire, CancellationToken ct = default);

    /// <summary>
    /// MODERATE-5 fix (2026-07-30) — the ledger's current
    /// <see cref="ScheduledTriggerFire.Outcome"/> for a
    /// <c>(trigger, window)</c>, or <c>null</c> when no row exists.
    ///
    /// <para>The tick calls this BEFORE taking the advisory lock so a window
    /// that already reached a TERMINAL outcome (<c>dispatched</c> /
    /// <c>failed</c>) is re-observed <b>silently</b>. Without it, a burnt
    /// window kept recomputing as due for the rest of its cadence (the trigger
    /// row's <c>LastFiredAt</c> only advances on SUCCESS), so every 60-second
    /// tick on every pod re-took the lock, lost the claim and wrote another
    /// <c>SCHEDULE.FIRE.SUPPRESSED</c> row — up to ~1440 duplicate audit rows
    /// per day per pod for ONE failed window. Correctness was never at risk
    /// (the ledger row is what refuses the re-dispatch); the audit trail was.
    /// </para>
    /// </summary>
    Task<string?> GetFireOutcomeAsync(
        Guid triggerId, string windowKey, CancellationToken ct = default);

    /// <summary>
    /// LOW-8 fix (2026-07-30) — the stale-claim sweep's feed: ledger rows
    /// still <c>Outcome = 'claimed'</c> whose <c>ClaimedAt</c> predates
    /// <paramref name="claimedBeforeUtc"/>, oldest first, at most
    /// <paramref name="limit"/> rows. These are fires whose owning pod died
    /// between the claim commit and the outcome stamp (or, for a manual fire,
    /// after the dispatch CAS). Bounded and cheap by construction — the read
    /// is a partial scan of a table that is empty of such rows in the normal
    /// case. NOT a claim: the sweeper must win
    /// <see cref="TryMarkFireAbandonedAsync"/> per row.
    /// </summary>
    Task<IReadOnlyList<ScheduledTriggerFire>> ListStaleClaimedFiresAsync(
        DateTime claimedBeforeUtc, int limit, CancellationToken ct = default);

    /// <summary>
    /// LOW-8 fix (2026-07-30) — burn a stale claim: a conditional CAS that
    /// stamps <c>Outcome = 'failed'</c> with
    /// <paramref name="detail"/> only while the row is still <c>claimed</c>
    /// (<c>DispatchedAt</c> is deliberately left untouched, so a burnt
    /// manual fire keeps its CAS marker). Exactly one pod's sweep gets
    /// <c>true</c> for a given row; every other pod gets <c>false</c> and does
    /// not emit. Stamping a terminal outcome is what makes the sweep
    /// emit-once rather than a per-tick drip, and it is explicitly NOT a
    /// retry: at-most-once means the window is burnt, and the next window is
    /// the recovery path.
    /// </summary>
    Task<bool> TryMarkFireAbandonedAsync(
        Guid fireId, string detail, CancellationToken ct = default);

    /// <summary>
    /// Stamp the claimed row's outcome (<c>dispatched</c> / <c>failed</c>)
    /// plus the dispatch instant, workflow instance id and failure detail.
    /// </summary>
    Task StampOutcomeAsync(
        Guid fireId, string outcome, string? workflowInstanceId, string? detail,
        DateTime? dispatchedAtUtc, CancellationToken ct = default);

    /// <summary>
    /// Bookkeeping after a successful dispatch: stamp
    /// <c>LastWindowKey</c> / <c>LastFiredAt</c> / <c>NextDueAt</c> on the
    /// trigger row. Informational — the ledger stays authoritative.
    /// </summary>
    Task StampTriggerFiredAsync(
        Guid triggerId, string windowKey, DateTime firedAtUtc, DateTime? nextDueAtUtc,
        CancellationToken ct = default);

    /// <summary>
    /// D8 run-now — <c>manual:{timestamp}</c> ledger rows claimed by the
    /// admin API whose dispatch has not been attempted
    /// (<c>Outcome = 'claimed'</c> AND <c>DispatchedAt IS NULL</c>), paired
    /// with their trigger rows, filtered to ENABLED triggers only
    /// (2026-07-29 contract decision: a disabled trigger's pending manual
    /// fires wait until the trigger is re-enabled; the admin API refuses new
    /// run-now claims on a disabled trigger with 409). This read is NOT a
    /// claim — the drain must win
    /// <see cref="TryClaimManualFireForDispatchAsync"/> for each row before
    /// dispatching it.
    /// </summary>
    Task<IReadOnlyList<(ScheduledTriggerFire Fire, ScheduledTrigger Trigger)>>
        ListPendingManualFiresAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// MAJOR-1 fix (2026-07-29) — the manual drain's at-most-once arbiter: a
    /// conditional CAS that stamps <c>DispatchedAt</c> while the row is still
    /// <c>Outcome = 'claimed'</c> and unstamped. Exactly one concurrent
    /// caller gets <c>true</c> (it owns the dispatch attempt); every other
    /// pod gets <c>false</c> and skips. A crash — or a failed terminal
    /// outcome stamp — after a won CAS BURNS the fire (the pending list
    /// filters <c>DispatchedAt IS NULL</c>), mirroring the cron path's
    /// burn-the-window semantics; a pending manual row can never be
    /// double-dispatched or re-dispatched in a loop.
    /// </summary>
    Task<bool> TryClaimManualFireForDispatchAsync(
        Guid fireId, DateTime attemptAtUtc, CancellationToken ct = default);

    /// <summary>
    /// Bounded ledger retention (D2): delete fire rows claimed before
    /// <paramref name="olderThanUtc"/>, at most <paramref name="maxRows"/>
    /// per call. Returns rows deleted.
    /// </summary>
    Task<int> PruneLedgerAsync(
        DateTime olderThanUtc, int maxRows = 1000, CancellationToken ct = default);
}
