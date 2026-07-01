using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 38-3 — EF-backed <see cref="ISlackOutboxRepository"/> bound to
/// <see cref="ControlPlaneDbContext"/>. Mirrors
/// <see cref="PlatformEmailOutboxRepository"/>'s claim-then-deliver shape so
/// <c>OutboxSlackSender</c> drains the queue exactly like the SMTP sender.
/// </summary>
public sealed class SlackOutboxRepository : ISlackOutboxRepository
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private readonly ControlPlaneDbContext _db;

    public SlackOutboxRepository(ControlPlaneDbContext db)
    {
        _db = db;
    }

    public async Task<SlackOutboxMessage> EnqueueAsync(
        SlackOutboxMessage msg, CancellationToken ct = default)
    {
        if (msg is null) throw new ArgumentNullException(nameof(msg));

        var now = DateTime.UtcNow;
        msg.Status = "pending";
        msg.Attempts = 0;
        msg.LastError = null;
        msg.SentAt = null;
        if (msg.NextAttemptAt == default) msg.NextAttemptAt = now;
        msg.CreatedAt = now;
        msg.UpdatedAt = now;
        if (msg.MaxAttempts <= 0) msg.MaxAttempts = 5;
        if (string.IsNullOrWhiteSpace(msg.MessageType)) msg.MessageType = "Info";

        _db.SlackOutbox.Add(msg);
        await _db.SaveChangesAsync(ct);
        return msg;
    }

    public async Task<SlackOutboxMessage?> ClaimNextPendingAsync(
        DateTime now, CancellationToken ct = default)
    {
        if (string.Equals(_db.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal))
        {
            return await ClaimViaPostgresAsync(now, ct);
        }

        return await ClaimViaNaivePathAsync(now, ct);
    }

    private async Task<SlackOutboxMessage?> ClaimViaPostgresAsync(DateTime now, CancellationToken ct)
    {
        var rows = await _db.SlackOutbox.FromSqlInterpolated($"""
            UPDATE slack_outbox
            SET "Status" = 'sending', "UpdatedAt" = {now}
            WHERE "Id" = (
                SELECT "Id" FROM slack_outbox
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

    private async Task<SlackOutboxMessage?> ClaimViaNaivePathAsync(DateTime now, CancellationToken ct)
    {
        var candidate = await _db.SlackOutbox
            .Where(m => m.Status == "pending" && m.NextAttemptAt <= now)
            .OrderBy(m => m.NextAttemptAt)
            .ThenBy(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (candidate is null) return null;

        candidate.Status = "sending";
        candidate.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return candidate;
    }

    public async Task MarkSentAsync(Guid id, CancellationToken ct = default)
    {
        var msg = await _db.SlackOutbox.FindAsync(new object[] { id }, ct);
        if (msg is null) return;

        var now = DateTime.UtcNow;
        msg.Status = "sent";
        msg.LastError = null;
        msg.SentAt = now;
        msg.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<SlackOutboxMessage?> MarkFailedAsync(
        Guid id, string error, TimeSpan? backoff, CancellationToken ct = default)
    {
        var msg = await _db.SlackOutbox.FindAsync(new object[] { id }, ct);
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
        }

        await _db.SaveChangesAsync(ct);
        return msg;
    }

    public async Task<SlackOutboxMessage?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.SlackOutbox.FindAsync(new object[] { id }, ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.SlackOutbox.FindAsync(new object[] { id }, ct);
        if (row is null) return;
        _db.SlackOutbox.Remove(row);
        await _db.SaveChangesAsync(ct);
    }
}
