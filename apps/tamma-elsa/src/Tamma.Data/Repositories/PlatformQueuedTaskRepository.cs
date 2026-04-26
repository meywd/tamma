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
        task.ClaimedBy = null;
        task.UnprocessableAt = null;
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
            return await ReserveViaPostgresAsync(workerId, ct);
        }

        return await ReserveViaNaivePathAsync(workerId, ct);
    }

    private async Task<PlatformQueuedTask?> ReserveViaPostgresAsync(
        string workerId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Round-2 M8 — persist the worker id alongside the timestamp so
        // ops can identify the original claimant on a stuck row. The
        // workerId is parameterised through FromSqlInterpolated so the
        // value is bound (no SQL injection).
        //
        // Single-statement atomic reserve: grab the oldest pending row,
        // flip to processing, return every column. FOR UPDATE SKIP LOCKED
        // lets concurrent workers in a multi-pod deploy each pick a
        // different row without retrying on lock contention.
        var rows = await _db.PlatformQueuedTasks.FromSqlInterpolated($"""
            UPDATE platform_queued_tasks
            SET "Status" = 'processing',
                "ClaimedAt" = {now},
                "ClaimedBy" = {workerId},
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

    private async Task<PlatformQueuedTask?> ReserveViaNaivePathAsync(
        string workerId, CancellationToken ct)
    {
        var task = await _db.PlatformQueuedTasks
            .Where(t => t.Status == "pending")
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (task is null) return null;

        var now = DateTime.UtcNow;
        task.Status = "processing";
        task.ClaimedAt = now;
        task.ClaimedBy = workerId;
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
            // Round-2 M8 — clear the worker id when the row returns to
            // the pending pool so a future claim is unambiguously the
            // new owner.
            task.ClaimedBy = null;
        }

        await _db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<PlatformQueuedTask?> ParkUnprocessableAsync(
        Guid id, string reason, int maxRetries, CancellationToken ct = default)
    {
        var task = await _db.PlatformQueuedTasks.FindAsync(new object[] { id }, ct);
        if (task is null) return null;

        var now = DateTime.UtcNow;
        task.RetryCount += 1;
        task.Error = reason;
        task.UpdatedAt = now;
        task.UnprocessableAt = now;

        // Round-2 H8 — only fall through to dead_letter once the retry
        // ceiling is hit. The intent is that an absent handler is a
        // deploy gap, not a permanent malformed-payload condition; the
        // next deploy that registers the handler picks the row up.
        if (task.RetryCount >= Math.Max(1, maxRetries))
        {
            task.Status = "dead_letter";
        }
        else
        {
            task.Status = "pending";
            task.ClaimedAt = null;
            task.ClaimedBy = null;
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
        if (string.Equals(_db.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal))
        {
            return await ReapStaleViaPostgresAsync(visibilityTimeout, maxRetries, ct);
        }

        return await ReapStaleViaNaivePathAsync(visibilityTimeout, maxRetries, ct);
    }

    /// <summary>
    /// Round-2 M9 — atomic, multi-pod-safe reaper. Uses
    /// <c>FOR UPDATE SKIP LOCKED</c> in a subquery so two reapers
    /// across pods can't double-decrement the same row's retry count
    /// (which used to dead-letter rows that were one retry shy of
    /// recovery). Single statement; no read-modify-write loop.
    ///
    /// <para>The CASE expression mirrors the FailAsync semantics: if
    /// <c>RetryCount + 1</c> reaches <paramref name="maxRetries"/>,
    /// the row flips to <c>dead_letter</c>; otherwise it returns to
    /// <c>pending</c> with <c>ClaimedAt</c> cleared so the next
    /// reservation may pick it up. <c>ClaimedBy</c> is also cleared
    /// (Round-2 M8) so the new claimant is unambiguous.</para>
    /// </summary>
    private async Task<int> ReapStaleViaPostgresAsync(
        TimeSpan visibilityTimeout, int maxRetries, CancellationToken ct)
    {
        var threshold = DateTime.UtcNow - visibilityTimeout;
        var now = DateTime.UtcNow;
        var ceiling = Math.Max(1, maxRetries);
        var reason =
            $"reaped after {visibilityTimeout.TotalSeconds:0}s visibility timeout";

        // Single UPDATE with FOR UPDATE SKIP LOCKED in the subquery so
        // two pods reaping concurrently each grab a disjoint set.
        // ExecuteSqlInterpolatedAsync returns the row count of the
        // outer UPDATE — exactly what we need to log.
        var rowCount = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE platform_queued_tasks
            SET "Status" = CASE
                    WHEN "RetryCount" + 1 >= {ceiling} THEN 'dead_letter'
                    ELSE 'pending'
                END,
                "RetryCount" = "RetryCount" + 1,
                "Error" = {reason},
                "ClaimedAt" = CASE
                    WHEN "RetryCount" + 1 >= {ceiling} THEN "ClaimedAt"
                    ELSE NULL
                END,
                "ClaimedBy" = CASE
                    WHEN "RetryCount" + 1 >= {ceiling} THEN "ClaimedBy"
                    ELSE NULL
                END,
                "UpdatedAt" = {now}
            WHERE "Id" IN (
                SELECT "Id" FROM platform_queued_tasks
                WHERE "Status" = 'processing'
                  AND ("ClaimedAt" IS NULL OR "ClaimedAt" < {threshold})
                FOR UPDATE SKIP LOCKED
            )
            """, ct);
        return rowCount;
    }

    /// <summary>
    /// Naive single-writer fallback for the EF InMemory provider used
    /// by unit tests. Mirrors the Postgres path's semantics; safe
    /// because in-process tests never run two reapers concurrently.
    /// </summary>
    private async Task<int> ReapStaleViaNaivePathAsync(
        TimeSpan visibilityTimeout, int maxRetries, CancellationToken ct)
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
                task.ClaimedBy = null;
            }
        }
        await _db.SaveChangesAsync(ct);
        return stale.Count;
    }
}
