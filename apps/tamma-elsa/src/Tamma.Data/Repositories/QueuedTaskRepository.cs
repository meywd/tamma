using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF-backed persistence for the <see cref="QueuedTask"/> queue. Pure CRUD;
/// all business policy (polling cadence, retry ceiling, handler dispatch)
/// lives in <c>Tamma.Api.Services.TaskQueue</c>.
///
/// <para>
/// Ported from the deleted TypeScript <c>InMemoryTaskQueue</c> in
/// <c>packages/api/src/services/in-memory-task-queue.ts</c>.
/// </para>
/// </summary>
public class QueuedTaskRepository(TammaDbContext db) : IQueuedTaskRepository
{
    public async Task<QueuedTask> EnqueueAsync(QueuedTask task, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        task.Status = "pending";
        task.RetryCount = 0;
        task.Error = null;
        task.CreatedAt = now;
        task.UpdatedAt = now;

        db.QueuedTasks.Add(task);
        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<QueuedTask?> GetAsync(Guid id, CancellationToken ct = default)
        => await db.QueuedTasks.FindAsync(new object[] { id }, ct);

    public async Task<List<QueuedTask>> ListPendingAsync(
        Guid? tenantId, int limit, CancellationToken ct = default)
    {
        var query = db.QueuedTasks.AsQueryable()
            .Where(t => t.Status == "pending");

        if (tenantId.HasValue)
        {
            var tid = tenantId.Value;
            query = query.Where(t => t.TenantId == tid);
        }

        return await query
            .OrderBy(t => t.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<QueuedTask?> MarkProcessingAsync(Guid id, CancellationToken ct = default)
    {
        var task = await db.QueuedTasks.FindAsync(new object[] { id }, ct);
        if (task is null) return null;
        if (task.Status != "pending") return null;

        var now = DateTime.UtcNow;
        task.Status = "processing";
        task.ClaimedAt = now; // Audit finding 026 — drives the reaper.
        task.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return task;
    }

    /// <summary>
    /// Audit finding 026 — visibility-timeout reaper. Resets rows stuck in
    /// <c>processing</c> for longer than <paramref name="visibilityTimeout"/>
    /// back to <c>pending</c> (or <c>failed</c> when retry ceiling reached)
    /// so a worker that died mid-task does not leave zombies forever.
    /// Returns the number of rows reaped.
    /// </summary>
    public async Task<int> ReapStaleProcessingAsync(
        TimeSpan visibilityTimeout, int maxRetries, CancellationToken ct = default)
    {
        var threshold = DateTime.UtcNow - visibilityTimeout;
        var stale = await db.QueuedTasks
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
        await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    public async Task MarkCompletedAsync(Guid id, CancellationToken ct = default)
    {
        var task = await db.QueuedTasks.FindAsync(new object[] { id }, ct);
        if (task is null) return;

        task.Status = "completed";
        task.Error = null;
        task.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkFailedAsync(Guid id, string error, CancellationToken ct = default)
    {
        var task = await db.QueuedTasks.FindAsync(new object[] { id }, ct);
        if (task is null) return;

        task.Status = "failed";
        task.Error = error;
        task.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<QueuedTask?> IncrementRetryAndRequeueAsync(
        Guid id, string error, CancellationToken ct = default)
    {
        var task = await db.QueuedTasks.FindAsync(new object[] { id }, ct);
        if (task is null) return null;

        task.RetryCount += 1;
        task.Status = "pending";
        task.Error = error;
        task.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return task;
    }
}
