using Tamma.Data.Entities;

namespace Tamma.Api.Services.TaskQueue;

/// <summary>
/// Multi-tenant task queue surface used by the API layer. Wraps
/// <see cref="Tamma.Data.Repositories.IQueuedTaskRepository"/> with tenant
/// scoping derived from <see cref="Tamma.Data.ITenantContext"/>.
///
/// <para>
/// Ported from the deleted TypeScript <c>ITaskQueue</c>
/// (<c>packages/api/src/services/task-queue.ts</c>). The C# surface drops the
/// in-memory <c>dequeue</c> helper because atomic pending→processing
/// transitions now happen in <see cref="TaskQueueProcessor"/> via the
/// repository's <c>MarkProcessingAsync</c>.
/// </para>
/// </summary>
public interface ITaskQueue
{
    /// <summary>
    /// Enqueue a new pending task. The tenant is resolved from the ambient
    /// <c>ITenantContext</c>; callers do not set <see cref="QueuedTask.TenantId"/>
    /// directly. Pass an explicit <paramref name="tenantIdOverride"/> to bypass
    /// the ambient context (used by the webhook handler, which derives tenancy
    /// from the GitHub installation ID, not the authenticated user).
    /// </summary>
    Task<QueuedTask> EnqueueAsync(
        string type,
        string payloadJson,
        long? installationId = null,
        Guid? tenantIdOverride = null,
        CancellationToken ct = default);

    /// <summary>
    /// Fetch a single task by id. Returns the row regardless of tenant
    /// (processor + observability tools may need cross-tenant reads); callers
    /// that want tenant isolation must filter on the returned tenant.
    /// </summary>
    Task<QueuedTask?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// List pending tasks for the ambient tenant. When the ambient tenant is
    /// <c>null</c> (system scope / self-hosted), returns tasks for every
    /// tenant — this is the lane the processor takes when it runs unscoped.
    /// </summary>
    Task<List<QueuedTask>> ListPendingAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>Claim a pending task. See repository for semantics.</summary>
    Task<QueuedTask?> MarkProcessingAsync(Guid id, CancellationToken ct = default);

    /// <summary>Mark a claimed task complete.</summary>
    Task MarkCompletedAsync(Guid id, CancellationToken ct = default);

    /// <summary>Mark a claimed task failed with an error message.</summary>
    Task MarkFailedAsync(Guid id, string error, CancellationToken ct = default);
}
