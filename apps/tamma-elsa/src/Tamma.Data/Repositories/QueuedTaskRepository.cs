using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF-backed persistence for the <see cref="QueuedTask"/> queue.
///
/// <para>In the Epic 28 target model each tenant's queue lives in its own
/// DB; the processor round-robins across tenants. Transitional impl:
/// enqueue uses <see cref="ITenantDbContextFactory"/> when a tenant is
/// known; processor-facing cross-tenant operations (list-pending across
/// tenants, reaper, mark-*, by-id lookup) use
/// <see cref="ControlPlaneDbContext"/> because they walk the shared
/// queue as platform-infrastructure.</para>
/// </summary>
public class QueuedTaskRepository(
    ITenantDbContextFactory tenantDbFactory,
    ControlPlaneDbContext cp) : IQueuedTaskRepository
{
    public async Task<QueuedTask> EnqueueAsync(QueuedTask task, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        task.Status = "pending";
        task.RetryCount = 0;
        task.Error = null;
        task.CreatedAt = now;
        task.UpdatedAt = now;

        if (task.TenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            db.QueuedTasks.Add(task);
            await db.SaveChangesAsync(ct);
            return task;
        }

        // Platform-scope task (no tenant).
        cp.QueuedTasks.Add(task);
        await cp.SaveChangesAsync(ct);
        return task;
    }

    public async Task<QueuedTask?> GetAsync(Guid id, CancellationToken ct = default)
        => await cp.QueuedTasks.FindAsync(new object[] { id }, ct);

    public async Task<List<QueuedTask>> ListPendingAsync(
        Guid? tenantId, int limit, CancellationToken ct = default)
    {
        if (tenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            return await db.QueuedTasks
                .Where(t => t.Status == "pending" && t.TenantId == tid)
                .OrderBy(t => t.CreatedAt)
                .Take(limit)
                .ToListAsync(ct);
        }

        // Cross-tenant processor path — CP.
        return await cp.QueuedTasks
            .Where(t => t.Status == "pending")
            .OrderBy(t => t.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<QueuedTask?> MarkProcessingAsync(Guid id, CancellationToken ct = default)
    {
        var task = await cp.QueuedTasks.FindAsync(new object[] { id }, ct);
        if (task is null) return null;
        if (task.Status != "pending") return null;

        var now = DateTime.UtcNow;
        task.Status = "processing";
        task.ClaimedAt = now;
        task.UpdatedAt = now;
        await cp.SaveChangesAsync(ct);
        return task;
    }

    public async Task<int> ReapStaleProcessingAsync(
        TimeSpan visibilityTimeout, int maxRetries, CancellationToken ct = default)
    {
        var threshold = DateTime.UtcNow - visibilityTimeout;
        var stale = await cp.QueuedTasks
            .Where(t => t.Status == "processing"
                && (t.ClaimedAt == null || t.ClaimedAt < threshold))
            .ToListAsync(ct);

        if (stale.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var task in stale)
        {
            task.RetryCount += 1;
            task.Error = $"reaped after {visibilityTimeout.TotalSeconds:0}s visibility timeout";
            task.UpdatedAt = now;

            if (task.RetryCount >= maxRetries)
            {
                task.Status = "failed";
            }
            else
            {
                task.Status = "pending";
                task.ClaimedAt = null;
            }
        }
        await cp.SaveChangesAsync(ct);
        return stale.Count;
    }

    public async Task MarkCompletedAsync(Guid id, CancellationToken ct = default)
    {
        var task = await cp.QueuedTasks.FindAsync(new object[] { id }, ct);
        if (task is null) return;

        task.Status = "completed";
        task.Error = null;
        task.UpdatedAt = DateTime.UtcNow;
        await cp.SaveChangesAsync(ct);
    }

    public async Task MarkFailedAsync(Guid id, string error, CancellationToken ct = default)
    {
        var task = await cp.QueuedTasks.FindAsync(new object[] { id }, ct);
        if (task is null) return;

        task.Status = "failed";
        task.Error = error;
        task.UpdatedAt = DateTime.UtcNow;
        await cp.SaveChangesAsync(ct);
    }

    public async Task<QueuedTask?> IncrementRetryAndRequeueAsync(
        Guid id, string error, CancellationToken ct = default)
    {
        var task = await cp.QueuedTasks.FindAsync(new object[] { id }, ct);
        if (task is null) return null;

        task.RetryCount += 1;
        task.Status = "pending";
        task.Error = error;
        task.UpdatedAt = DateTime.UtcNow;
        await cp.SaveChangesAsync(ct);
        return task;
    }
}
