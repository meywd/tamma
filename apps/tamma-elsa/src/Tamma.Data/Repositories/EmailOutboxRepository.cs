using Tamma.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF-backed <see cref="IEmailOutboxRepository"/>.
///
/// <para>In the Epic 28 target architecture each tenant has its own outbox
/// table in its own DB; the sender polls every tenant's outbox in round-robin
/// fashion. During the transition the physical table is shared. Enqueue
/// operations that know their tenant go through
/// <see cref="ITenantDbContextFactory"/>; sender-facing cross-tenant
/// operations (claim-next, mark-sent, mark-failed, by-id lookups) use
/// <see cref="ControlPlaneDbContext"/> because they traverse the shared
/// outbox as platform-infrastructure.</para>
/// </summary>
public class EmailOutboxRepository(
    ITenantDbContextFactory tenantDbFactory,
    ControlPlaneDbContext cp) : IEmailOutboxRepository
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

        if (msg.TenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            db.EmailOutbox.Add(msg);
            await db.SaveChangesAsync(ct);
            return msg;
        }

        // Platform-scope enqueue (system-generated emails with no tenant).
        cp.EmailOutbox.Add(msg);
        await cp.SaveChangesAsync(ct);
        return msg;
    }

    public async Task<EmailOutboxMessage?> ClaimNextPendingAsync(
        DateTime now, CancellationToken ct = default)
    {
        // Sender path — cross-tenant scan, platform infrastructure.
        if (string.Equals(cp.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal))
        {
            return await ClaimViaPostgresAsync(cp, now, ct);
        }
        return await ClaimViaNaivePathAsync(cp, now, ct);
    }

    private static async Task<EmailOutboxMessage?> ClaimViaPostgresAsync(
        ControlPlaneDbContext db, DateTime now, CancellationToken ct)
    {
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

    private static async Task<EmailOutboxMessage?> ClaimViaNaivePathAsync(
        ControlPlaneDbContext db, DateTime now, CancellationToken ct)
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
        var msg = await cp.EmailOutbox.FindAsync(new object[] { id }, ct);
        if (msg is null) return;

        var now = DateTime.UtcNow;
        msg.Status = "sent";
        msg.LastError = null;
        msg.SentAt = now;
        msg.UpdatedAt = now;
        await cp.SaveChangesAsync(ct);
    }

    public async Task<EmailOutboxMessage?> MarkFailedAsync(
        Guid id, string error, TimeSpan? backoff, CancellationToken ct = default)
    {
        var msg = await cp.EmailOutbox.FindAsync(new object[] { id }, ct);
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

        await cp.SaveChangesAsync(ct);
        return msg;
    }

    public async Task<EmailOutboxMessage?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await cp.EmailOutbox.FindAsync(new object[] { id }, ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var row = await cp.EmailOutbox.FindAsync(new object[] { id }, ct);
        if (row is null) return;
        cp.EmailOutbox.Remove(row);
        await cp.SaveChangesAsync(ct);
    }
}
