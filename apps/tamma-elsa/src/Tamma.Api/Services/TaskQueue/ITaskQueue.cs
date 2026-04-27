using Tamma.Data.Entities;

namespace Tamma.Api.Services.TaskQueue;

/// <summary>
/// Tenant-scoped task queue surface used by the API layer. Wraps
/// <see cref="Tamma.Data.Repositories.IQueuedTaskRepository"/> with
/// tenant scoping derived from <see cref="Tamma.Data.ITenantContext"/>.
///
/// <para>Story 28-1 PR B — this surface is now <b>tenant-scoped only</b>.
/// Platform-scope tasks (tenant provisioning, secret retire, GitHub
/// orphan webhooks) go straight to
/// <see cref="Tamma.Data.Repositories.IPlatformQueuedTaskRepository"/>
/// instead. The decision matrix is in
/// <c>.dev/decisions/story-28-1-design-calls.md</c> §5 plus the commit
/// body of PR B itself.</para>
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
    /// Enqueue a new pending task for a tenant. The tenant is resolved
    /// from the ambient <see cref="Tamma.Data.ITenantContext"/> unless
    /// <paramref name="tenantIdOverride"/> is supplied. If neither is
    /// available the call throws — platform-scope callers must use
    /// <see cref="Tamma.Data.Repositories.IPlatformQueuedTaskRepository"/>
    /// instead.
    /// </summary>
    Task<QueuedTask> EnqueueAsync(
        string type,
        string payloadJson,
        long? installationId = null,
        Guid? tenantIdOverride = null,
        CancellationToken ct = default);

    /// <summary>
    /// Fetch a single task by id from the supplied tenant's queue.
    /// Tenant id is required since the per-tenant queue lives in the
    /// per-tenant DB.
    /// </summary>
    Task<QueuedTask?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default);

    /// <summary>
    /// List pending tasks for the supplied tenant. Tenant id is
    /// required since the per-tenant queue lives in the per-tenant DB.
    /// </summary>
    Task<List<QueuedTask>> ListPendingAsync(Guid tenantId, int limit = 20, CancellationToken ct = default);

    /// <summary>Claim a pending task. See repository for semantics.</summary>
    Task<QueuedTask?> MarkProcessingAsync(Guid tenantId, Guid id, CancellationToken ct = default);

    /// <summary>Mark a claimed task complete.</summary>
    Task MarkCompletedAsync(Guid tenantId, Guid id, CancellationToken ct = default);

    /// <summary>Mark a claimed task failed with an error message.</summary>
    Task MarkFailedAsync(Guid tenantId, Guid id, string error, CancellationToken ct = default);
}
