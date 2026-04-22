using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF-backed <see cref="IPlatformQueuedTaskRepository"/> bound to
/// <see cref="ControlPlaneDbContext"/>. Mirrors
/// <see cref="QueuedTaskRepository"/> on the legacy single-DB context,
/// with two extras:
/// <list type="bullet">
///   <item><description>A Postgres-native <c>FOR UPDATE SKIP LOCKED</c>
///     reservation path that lets multiple workers across pods drain the
///     CP queue concurrently without double-processing.</description></item>
///   <item><description>A first-class <c>dead_letter</c> terminal state
///     for tasks that can never succeed (no handler registered,
///     malformed payload, retry ceiling exhausted).</description></item>
/// </list>
/// </summary>
public sealed class PlatformQueuedTaskRepository : IPlatformQueuedTaskRepository
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private readonly ControlPlaneDbContext _db;

    public PlatformQueuedTaskRepository(ControlPlaneDbContext db)
    {
        _db = db;
    }

    public async Task<PlatformQueuedTask> EnqueueAsync(
        PlatformQueuedTask task, CancellationToken ct = default)
    {
        if (task is null) throw new ArgumentNullException(nameof(task));
        if (string.IsNullOrWhiteSpace(task.Type))
            throw new ArgumentException("PlatformQueuedTask.Type is required", nameof(task));

        var now = DateTime.UtcNow;
        task.Status = "pending";
        task.RetryCount = 0;
        task.Error = null;
        task.ClaimedAt = null;
        task.CreatedAt = now;
        task.UpdatedAt = now;

        _db.PlatformQueuedTasks.Add(task);
        await _db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<PlatformQueuedTask?> ReserveNextAsync(
        string workerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("workerId is required", nameof(workerId));

        if (string.Equals(_db.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal))
        {
            return await ReserveViaPostgresAsync(ct);
        }

        return await ReserveViaNaivePathAsync(ct);
    }

    private async Task<PlatformQueuedTask?> ReserveViaPostgresAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Single-statement atomic reserve: grab the oldest pending row,
        // flip to processing, return every column. FOR UPDATE SKIP LOCKED
        // lets concurrent workers in a multi-pod deploy each pick a
        // different row without retrying on lock contention.
        var rows = await _db.PlatformQueuedTasks.FromSqlInterpolated($"""
            UPDATE platform_queued_tasks
            SET "Status" = 'processing',
                "ClaimedAt" = {now},
                "UpdatedAt" = {now}
            WHERE "Id" = (
                SELECT "Id" FROM platform_queued_tasks
                WHERE "Status" = 'pending'
                ORDER BY "CreatedAt" ASC
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING *
            """).AsNoTracking().ToListAsync(ct);

        return rows.FirstOrDefault();
    }

    private async Task<PlatformQueuedTask?> ReserveViaNaivePathAsync(CancellationToken ct)
    {
        var task = await _db.PlatformQueuedTasks
            .Where(t => t.Status == "pending")
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (task is null) return null;

        var now = DateTime.UtcNow;
        task.Status = "processing";
        task.ClaimedAt = now;
        task.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return task;
    }

    public async Task CompleteAsync(Guid id, CancellationToken ct = default)
    {
        var task = await _db.PlatformQueuedTasks.FindAsync(new object[] { id }, ct);
        if (task is null) return;

        task.Status = "completed";
        task.Error = null;
        task.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<PlatformQueuedTask?> FailAsync(
        Guid id, string error, int maxRetries, CancellationToken ct = default)
    {
        var task = await _db.PlatformQueuedTasks.FindAsync(new object[] { id }, ct);
        if (task is null) return null;

        var now = DateTime.UtcNow;
        task.RetryCount += 1;
        task.Error = error;
        task.UpdatedAt = now;

        if (task.RetryCount >= Math.Max(1, maxRetries))
        {
            task.Status = "dead_letter";
        }
        else
        {
            task.Status = "pending";
            task.ClaimedAt = null;
        }

        await _db.SaveChangesAsync(ct);
        return task;
    }

    public async Task DeadLetterAsync(Guid id, string error, CancellationToken ct = default)
    {
        var task = await _db.PlatformQueuedTasks.FindAsync(new object[] { id }, ct);
        if (task is null) return;

        task.Status = "dead_letter";
        task.Error = error;
        task.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<PlatformQueuedTask?> GetAsync(Guid id, CancellationToken ct = default)
        => await _db.PlatformQueuedTasks.FindAsync(new object[] { id }, ct);

    public async Task<int> ReapStaleProcessingAsync(
        TimeSpan visibilityTimeout, int maxRetries, CancellationToken ct = default)
    {
        var threshold = DateTime.UtcNow - visibilityTimeout;
        var stale = await _db.PlatformQueuedTasks
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

            if (task.RetryCount >= Math.Max(1, maxRetries))
            {
                task.Status = "dead_letter";
            }
            else
            {
                task.Status = "pending";
                task.ClaimedAt = null;
            }
        }
        await _db.SaveChangesAsync(ct);
        return stale.Count;
    }
}
