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
    /// </summary>
    Task<bool> TryClaimFireAsync(ScheduledTriggerFire fire, CancellationToken ct = default);

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
    /// admin API but not yet dispatched, paired with their (enabled) trigger
    /// rows. The tick drains these with the same dispatch + stamp path as
    /// cron windows.
    /// </summary>
    Task<IReadOnlyList<(ScheduledTriggerFire Fire, ScheduledTrigger Trigger)>>
        ListPendingManualFiresAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Bounded ledger retention (D2): delete fire rows claimed before
    /// <paramref name="olderThanUtc"/>, at most <paramref name="maxRows"/>
    /// per call. Returns rows deleted.
    /// </summary>
    Task<int> PruneLedgerAsync(
        DateTime olderThanUtc, int maxRows = 1000, CancellationToken ct = default);
}
