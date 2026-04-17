using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence port for the multi-tenant task queue. Ported from the deleted
/// TypeScript <c>ITaskQueue</c> in <c>packages/api/src/services/task-queue.ts</c>.
///
/// <para>
/// This layer is pure CRUD — no polling, no retry semantics, no locking beyond
/// what EF Core provides. The <c>DbTaskQueue</c> service layer adds tenant
/// scoping; the <c>TaskQueueProcessor</c> hosted service adds the processing
/// loop and retry policy.
/// </para>
/// </summary>
public interface IQueuedTaskRepository
{
    /// <summary>
    /// Insert a new row in <c>pending</c> status. The caller is responsible for
    /// populating <see cref="QueuedTask.Type"/> + payload; everything else is
    /// filled in by this method.
    /// </summary>
    Task<QueuedTask> EnqueueAsync(QueuedTask task, CancellationToken ct = default);

    /// <summary>Fetch a single task by primary key (or null).</summary>
    Task<QueuedTask?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Return pending tasks in FIFO order (oldest first). When
    /// <paramref name="tenantId"/> is supplied only tasks belonging to that
    /// tenant are returned; otherwise every pending task is returned regardless
    /// of tenant (for the self-hosted processor path).
    /// </summary>
    Task<List<QueuedTask>> ListPendingAsync(Guid? tenantId, int limit, CancellationToken ct = default);

    /// <summary>
    /// Transition a task from <c>pending</c> → <c>processing</c>. Returns the
    /// updated row, or <c>null</c> if the id was unknown or the row was not in
    /// pending state (already claimed by another worker).
    /// </summary>
    Task<QueuedTask?> MarkProcessingAsync(Guid id, CancellationToken ct = default);

    /// <summary>Transition to <c>completed</c>.</summary>
    Task MarkCompletedAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Transition to <c>failed</c> and set <see cref="QueuedTask.Error"/>.
    /// </summary>
    Task MarkFailedAsync(Guid id, string error, CancellationToken ct = default);

    /// <summary>
    /// Increment <see cref="QueuedTask.RetryCount"/> and reset status back to
    /// <c>pending</c> so the processor can re-claim it on its next cycle.
    /// </summary>
    Task<QueuedTask?> IncrementRetryAndRequeueAsync(Guid id, string error, CancellationToken ct = default);
}
