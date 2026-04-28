using Tamma.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF-backed persistence for the per-tenant <see cref="QueuedTask"/>
/// queue. Strictly tenant-scoped — every operation routes through
/// <see cref="ITenantDbContextFactory"/> so the read/write hits the
/// per-tenant DB. Platform-scope tasks (tenant provisioning, secret
/// retire, GitHub orphan webhook) use
/// <see cref="IPlatformQueuedTaskRepository"/> instead — see
/// <c>.dev/decisions/story-28-1-design-calls.md</c> §5.
///
/// <para>During the Story 28-1 transition the per-tenant
/// <c>queued_tasks</c> table physically still co-resides on the CP DB —
/// PR D moves it to per-tenant DBs. Until then a tenant-bound DB
/// context built by the factory still sees the same shared table; the
/// split here is logical so the eventual physical move is mechanical.</para>
/// </summary>
public class QueuedTaskRepository(
    ITenantDbContextFactory tenantDbFactory,
    ControlPlaneDbContext cp) : IQueuedTaskRepository
{
    public async Task<QueuedTask> EnqueueAsync(QueuedTask task, CancellationToken ct = default)
    {
        if (task.TenantId is not Guid tid || tid == Guid.Empty)
        {
            throw new ArgumentException(
                "QueuedTaskRepository requires a non-empty TenantId. " +
                "Platform-scope tasks (provisioning, retire, orphan " +
                "webhook) must go through IPlatformQueuedTaskRepository.",
                nameof(task));
        }

        var now = DateTime.UtcNow;
        task.Status = "pending";
        task.RetryCount = 0;
        task.Error = null;
        task.CreatedAt = now;
        task.UpdatedAt = now;

        await using var db = await tenantDbFactory.CreateAsync(tid, ct);
        db.QueuedTasks.Add(task);
        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<QueuedTask?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
        // Story 28-1 PR B fix (wave-4 review H2) — FindAsync keys on PK
        // only. While the per-tenant queue physically still co-resides
        // on the CP DB, tenant A's call could read tenant B's row.
        // Predicate-based lookup makes the tenant id a hard guard.
        return await db.QueuedTasks
            .Where(t => t.Id == id && t.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<QueuedTask>> ListPendingAsync(
        Guid tenantId, int limit, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
        return await db.QueuedTasks
            .Where(t => t.Status == "pending" && t.TenantId == tenantId)
            .OrderBy(t => t.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<List<QueuedTask>> ListPendingFromAnyTenantAsync(
        int batchSizePerTenant, CancellationToken ct = default)
    {
        // Snapshot the active tenant set first. Cheap (single SELECT
        // bounded by the tenants table) relative to the per-task handler
        // dispatch this drain pass precedes.
        var activeTenantIds = await cp.Tenants
            .AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(ct);

        var aggregate = new List<QueuedTask>(capacity: activeTenantIds.Count * batchSizePerTenant);
        foreach (var tid in activeTenantIds)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var rows = await ListPendingAsync(tid, batchSizePerTenant, ct);
                aggregate.AddRange(rows);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Tenant might be mid-deletion or have a transient
                // connection failure; don't let one tenant's outage
                // starve the rest. The caller logs and the next poll
                // re-tries. Wave-4 review M2 — cooperative cancellation
                // must propagate, not be swallowed as a "tenant outage".
                continue;
            }
        }

        return aggregate;
    }

    public async Task<QueuedTask?> MarkProcessingAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
        // Story 28-1 PR B fix (wave-4 review H2) — see GetAsync.
        var task = await db.QueuedTasks
            .Where(t => t.Id == id && t.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
        if (task is null) return null;
        if (task.Status != "pending") return null;

        var now = DateTime.UtcNow;
        task.Status = "processing";
        task.ClaimedAt = now;
        task.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<int> ReapStaleProcessingAsync(
        Guid tenantId, TimeSpan visibilityTimeout, int maxRetries, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
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

    public async Task<int> ReapStaleProcessingAcrossAllTenantsAsync(
        TimeSpan visibilityTimeout, int maxRetries, CancellationToken ct = default)
    {
        var activeTenantIds = await cp.Tenants
            .AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(ct);

        var total = 0;
        foreach (var tid in activeTenantIds)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                total += await ReapStaleProcessingAsync(tid, visibilityTimeout, maxRetries, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Don't let one tenant's outage starve the reaper for
                // the rest; the caller logs and the next poll re-tries.
                // Wave-4 review M2 — cooperative cancellation must
                // propagate, not be swallowed as a "tenant outage".
                continue;
            }
        }
        return total;
    }

    public async Task MarkCompletedAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
        // Story 28-1 PR B fix (wave-4 review H2) — see GetAsync.
        var task = await db.QueuedTasks
            .Where(t => t.Id == id && t.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
        if (task is null) return;

        task.Status = "completed";
        task.Error = null;
        task.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkFailedAsync(Guid tenantId, Guid id, string error, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
        // Story 28-1 PR B fix (wave-4 review H2) — see GetAsync.
        var task = await db.QueuedTasks
            .Where(t => t.Id == id && t.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
        if (task is null) return;

        task.Status = "failed";
        task.Error = error;
        task.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<QueuedTask?> IncrementRetryAndRequeueAsync(
        Guid tenantId, Guid id, string error, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
        // Story 28-1 PR B fix (wave-4 review H2) — see GetAsync.
        var task = await db.QueuedTasks
            .Where(t => t.Id == id && t.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
        if (task is null) return null;

        task.RetryCount += 1;
        task.Status = "pending";
        task.Error = error;
        task.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return task;
    }
}
