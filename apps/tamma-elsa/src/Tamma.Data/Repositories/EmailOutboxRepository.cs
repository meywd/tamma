using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF-backed <see cref="IEmailOutboxRepository"/>. Two different claim paths:
/// <list type="bullet">
///   <item><description>Postgres (production): <c>UPDATE ... RETURNING</c>
///     with <c>FOR UPDATE SKIP LOCKED</c> in a subquery so concurrent senders
///     on a cluster never pick the same row twice. Single-statement, no race.</description></item>
///   <item><description>Other providers (EF InMemory, SQLite-in-test): naive
///     find-order-first + update semantics. Safe for a single writer; used by
///     unit tests only.</description></item>
/// </list>
/// </summary>
public class EmailOutboxRepository(TammaDbContext db) : IEmailOutboxRepository
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<EmailOutboxMessage> EnqueueAsync(
        EmailOutboxMessage msg, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        msg.Status = "pending";
        msg.Attempts = 0;
        msg.LastError = null;
        msg.SentAt = null;
        if (msg.NextAttemptAt == default) msg.NextAttemptAt = now;
        msg.CreatedAt = now;
        msg.UpdatedAt = now;
        if (msg.MaxAttempts <= 0) msg.MaxAttempts = 5;

        db.EmailOutbox.Add(msg);
        await db.SaveChangesAsync(ct);
        return msg;
    }

    public async Task<EmailOutboxMessage?> ClaimNextPendingAsync(
        DateTime now, CancellationToken ct = default)
    {
        if (string.Equals(db.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal))
        {
            return await ClaimViaPostgresAsync(now, ct);
        }

        return await ClaimViaNaivePathAsync(now, ct);
    }

    private async Task<EmailOutboxMessage?> ClaimViaPostgresAsync(
        DateTime now, CancellationToken ct)
    {
        // Single-statement atomic claim: grab the oldest due `pending` row,
        // flip it to `sending`, and return every column. FOR UPDATE SKIP
        // LOCKED lets concurrent senders in a multi-instance deploy each
        // pick a different row without retrying on lock contention.
        //
        // Parameter binding via FromSqlInterpolated prevents injection even
        // though `now` is a DateTime (not user input).
        var rows = await db.EmailOutbox.FromSqlInterpolated($"""
            UPDATE email_outbox
            SET "Status" = 'sending', "UpdatedAt" = {now}
            WHERE "Id" = (
                SELECT "Id" FROM email_outbox
                WHERE "Status" = 'pending'
                  AND "NextAttemptAt" <= {now}
                ORDER BY "NextAttemptAt" ASC, "CreatedAt" ASC
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING *
            """).AsNoTracking().ToListAsync(ct);

        return rows.FirstOrDefault();
    }

    private async Task<EmailOutboxMessage?> ClaimViaNaivePathAsync(
        DateTime now, CancellationToken ct)
    {
        var candidate = await db.EmailOutbox
            .Where(m => m.Status == "pending" && m.NextAttemptAt <= now)
            .OrderBy(m => m.NextAttemptAt)
            .ThenBy(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (candidate is null) return null;

        candidate.Status = "sending";
        candidate.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return candidate;
    }

    public async Task MarkSentAsync(Guid id, CancellationToken ct = default)
    {
        var msg = await db.EmailOutbox.FindAsync(new object[] { id }, ct);
        if (msg is null) return;

        var now = DateTime.UtcNow;
        msg.Status = "sent";
        msg.LastError = null;
        msg.SentAt = now;
        msg.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task<EmailOutboxMessage?> MarkFailedAsync(
        Guid id, string error, TimeSpan? backoff, CancellationToken ct = default)
    {
        var msg = await db.EmailOutbox.FindAsync(new object[] { id }, ct);
        if (msg is null) return null;

        var now = DateTime.UtcNow;
        msg.Attempts += 1;
        msg.LastError = error;
        msg.UpdatedAt = now;

        if (msg.Attempts < msg.MaxAttempts)
        {
            msg.Status = "pending";
            msg.NextAttemptAt = now + (backoff ?? TimeSpan.FromMinutes(1));
        }
        else
        {
            msg.Status = "failed";
            // Keep NextAttemptAt where it was — no more attempts are scheduled.
        }

        await db.SaveChangesAsync(ct);
        return msg;
    }

    public async Task<EmailOutboxMessage?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await db.EmailOutbox.FindAsync(new object[] { id }, ct);
}
