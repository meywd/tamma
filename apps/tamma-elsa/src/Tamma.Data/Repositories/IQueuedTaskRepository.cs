using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence port for the per-tenant task queue. Pure CRUD — no
/// polling, no retry semantics, no locking beyond what EF Core provides.
/// The <c>DbTaskQueue</c> service layer adds tenant scoping; the
/// <c>TaskQueueProcessor</c> hosted service adds the processing loop and
/// retry policy.
///
/// <para>Story 28-1 PR B — this repository is now <b>strictly
/// tenant-scoped</b>. Every operation requires a <c>tenantId</c> so the
/// implementation routes to the correct per-tenant DB. Cross-tenant
/// maintenance work (tenant provisioning, secret retire, GitHub orphan
/// webhook) goes through <see cref="IPlatformQueuedTaskRepository"/>
/// instead — see <c>.dev/decisions/story-28-1-design-calls.md</c> §5.</para>
/// </summary>
public interface IQueuedTaskRepository
{
    /// <summary>
    /// Insert a new row in <c>pending</c> status into the supplied
    /// tenant's queue. The caller is responsible for populating
    /// <see cref="QueuedTask.Type"/> + payload + a non-null
    /// <see cref="QueuedTask.TenantId"/>; everything else is filled in
    /// by this method.
    /// <para>Throws <see cref="ArgumentException"/> if
    /// <see cref="QueuedTask.TenantId"/> is null — platform-scope
    /// callers (provisioning, retire, orphan webhook) must use
    /// <see cref="IPlatformQueuedTaskRepository"/>.</para>
    /// </summary>
    Task<QueuedTask> EnqueueAsync(QueuedTask task, CancellationToken ct = default);

    /// <summary>Fetch a single task by primary key from the supplied tenant's
    /// queue (or null).</summary>
    Task<QueuedTask?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default);

    /// <summary>
    /// Return pending tasks for the supplied tenant in FIFO order
    /// (oldest first), capped at <paramref name="limit"/>.
    /// </summary>
    Task<List<QueuedTask>> ListPendingAsync(Guid tenantId, int limit, CancellationToken ct = default);

    /// <summary>
    /// Cross-tenant drain pass — list active tenants from the CP
    /// <c>tenants</c> table and call <see cref="ListPendingAsync"/> on
    /// each. Returns a flat list of pending tasks across all tenants
    /// (with each row's <see cref="QueuedTask.TenantId"/> populated for
    /// the caller's subsequent mark-* calls). Used by
    /// <c>TaskQueueProcessor</c> as its drain primitive.
    /// <para>The returned list is bounded by
    /// <paramref name="batchSizePerTenant"/> per tenant so a hot tenant
    /// can't starve the rest. The list-active-tenants query is bounded
    /// by the <c>tenants</c> table size and runs at the configured poll
    /// cadence — cheap relative to the per-task handler dispatch this
    /// drain pass precedes.</para>
    /// </summary>
    Task<List<QueuedTask>> ListPendingFromAnyTenantAsync(
        int batchSizePerTenant, CancellationToken ct = default);

    /// <summary>
    /// Transition a task from <c>pending</c> → <c>processing</c>. Returns the
    /// updated row, or <c>null</c> if the id was unknown or the row was not in
    /// pending state (already claimed by another worker).
    /// </summary>
    Task<QueuedTask?> MarkProcessingAsync(Guid tenantId, Guid id, CancellationToken ct = default);

    /// <summary>Transition to <c>completed</c>.</summary>
    Task MarkCompletedAsync(Guid tenantId, Guid id, CancellationToken ct = default);

    /// <summary>
    /// Transition to <c>failed</c> and set <see cref="QueuedTask.Error"/>.
    /// </summary>
    Task MarkFailedAsync(Guid tenantId, Guid id, string error, CancellationToken ct = default);

    /// <summary>
    /// Increment <see cref="QueuedTask.RetryCount"/> and reset status back to
    /// <c>pending</c> so the processor can re-claim it on its next cycle.
    /// </summary>
    Task<QueuedTask?> IncrementRetryAndRequeueAsync(Guid tenantId, Guid id, string error, CancellationToken ct = default);

    /// <summary>
    /// Audit finding 026 — visibility-timeout reaper for the supplied
    /// tenant's queue. Rows stuck in <c>processing</c> for longer than
    /// <paramref name="visibilityTimeout"/> are presumed orphaned by a
    /// dead worker and either re-queued (if retry budget remains) or
    /// marked <c>failed</c>. Returns the number of rows touched.
    /// </summary>
    Task<int> ReapStaleProcessingAsync(
        Guid tenantId, TimeSpan visibilityTimeout, int maxRetries, CancellationToken ct = default);

    /// <summary>
    /// Cross-tenant variant of <see cref="ReapStaleProcessingAsync"/> —
    /// runs the reaper against every active tenant and returns the
    /// total number of rows reaped. Used by <c>TaskQueueProcessor</c>
    /// each poll cycle so a worker that died mid-task on any tenant
    /// gets recovered.
    /// </summary>
    Task<int> ReapStaleProcessingAcrossAllTenantsAsync(
        TimeSpan visibilityTimeout, int maxRetries, CancellationToken ct = default);
}
