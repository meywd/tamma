using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF-backed <see cref="IPlatformEmailOutboxRepository"/> bound to
/// <see cref="ControlPlaneDbContext"/>. Mirrors
/// <see cref="EmailOutboxRepository"/>'s shape so the existing
/// <c>OutboxSmtpSender</c> can drain both the per-tenant and the
/// platform queues with one polling loop.
/// </summary>
public sealed class PlatformEmailOutboxRepository : IPlatformEmailOutboxRepository
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private readonly ControlPlaneDbContext _db;

    public PlatformEmailOutboxRepository(ControlPlaneDbContext db)
    {
        _db = db;
    }

    public async Task<PlatformEmailOutboxMessage> EnqueueAsync(
        PlatformEmailOutboxMessage msg, CancellationToken ct = default)
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

        _db.PlatformEmailOutbox.Add(msg);
        await _db.SaveChangesAsync(ct);
        return msg;
    }

    public async Task<PlatformEmailOutboxMessage?> ClaimNextPendingAsync(
        DateTime now, CancellationToken ct = default)
    {
        if (string.Equals(_db.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal))
        {
            return await ClaimViaPostgresAsync(now, ct);
        }

        return await ClaimViaNaivePathAsync(now, ct);
    }

    private async Task<PlatformEmailOutboxMessage?> ClaimViaPostgresAsync(
        DateTime now, CancellationToken ct)
    {
        var rows = await _db.PlatformEmailOutbox.FromSqlInterpolated($"""
            UPDATE platform_email_outbox
            SET "Status" = 'sending', "UpdatedAt" = {now}
            WHERE "Id" = (
                SELECT "Id" FROM platform_email_outbox
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

    private async Task<PlatformEmailOutboxMessage?> ClaimViaNaivePathAsync(
        DateTime now, CancellationToken ct)
    {
        var candidate = await _db.PlatformEmailOutbox
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

    public async Task<int> ReclaimStuckSendingAsync(
        DateTime now, TimeSpan leaseTimeout, CancellationToken ct = default)
    {
        var cutoff = now - leaseTimeout;

        if (string.Equals(_db.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal))
        {
            // Reset to 'pending' only — Attempts is left untouched so a stuck
            // row keeps its full retry budget (it was never attempted-to-
            // completion). NextAttemptAt was already <= claim-time, so the
            // reclaimed row is immediately due for the next claim pass.
            return await _db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE platform_email_outbox
                SET "Status" = 'pending', "UpdatedAt" = {now}
                WHERE "Status" = 'sending'
                  AND "UpdatedAt" < {cutoff}
                """, ct);
        }

        var stuck = await _db.PlatformEmailOutbox
            .Where(m => m.Status == "sending" && m.UpdatedAt < cutoff)
            .ToListAsync(ct);
        foreach (var m in stuck)
        {
            m.Status = "pending";
            m.UpdatedAt = now;
        }
        if (stuck.Count > 0) await _db.SaveChangesAsync(ct);
        return stuck.Count;
    }

    public async Task MarkSentAsync(Guid id, CancellationToken ct = default)
    {
        var msg = await _db.PlatformEmailOutbox.FindAsync(new object[] { id }, ct);
        if (msg is null) return;

        var now = DateTime.UtcNow;
        msg.Status = "sent";
        msg.LastError = null;
        msg.SentAt = now;
        msg.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<PlatformEmailOutboxMessage?> MarkFailedAsync(
        Guid id, string error, TimeSpan? backoff, CancellationToken ct = default)
    {
        var msg = await _db.PlatformEmailOutbox.FindAsync(new object[] { id }, ct);
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

    public async Task<PlatformEmailOutboxMessage> EnqueueWelcomeOnceAsync(
        Guid tenantId,
        string toAddress,
        string tenantName,
        string fromAddress,
        CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("tenantId is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(toAddress))
            throw new ArgumentException("toAddress is required.", nameof(toAddress));
        if (string.IsNullOrWhiteSpace(fromAddress))
            throw new ArgumentException("fromAddress is required.", nameof(fromAddress));

        // Pre-check: a non-failed welcome row already pending/sending/sent
        // for this tenant means the activity already ran (or is racing a
        // concurrent run). Return it unchanged — exactly-once-per-tenant.
        var existing = await _db.PlatformEmailOutbox
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId
                        && m.Template == WelcomeEmailContent.Template
                        && m.Status != "failed")
            .OrderBy(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        var (subject, html, text) = WelcomeEmailContent.Render(tenantName);
        var now = DateTime.UtcNow;
        var msg = new PlatformEmailOutboxMessage
        {
            TenantId = tenantId,
            Template = WelcomeEmailContent.Template,
            ToAddress = toAddress,
            Subject = subject,
            HtmlBody = html,
            TextBody = text,
            FromAddress = fromAddress,
            Status = "pending",
            Attempts = 0,
            MaxAttempts = 5,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.PlatformEmailOutbox.Add(msg);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Race: a concurrent run inserted the welcome row between our
            // pre-check and SaveChanges. The partial unique index
            // (TenantId, Template) WHERE Status <> 'failed' rejected the
            // duplicate. Detach our losing entity and return the winner so
            // the workflow step is replay-safe.
            _db.Entry(msg).State = EntityState.Detached;
            var winner = await _db.PlatformEmailOutbox
                .AsNoTracking()
                .Where(m => m.TenantId == tenantId
                            && m.Template == WelcomeEmailContent.Template
                            && m.Status != "failed")
                .OrderBy(m => m.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (winner is not null) return winner;
            throw;
        }

        return msg;
    }

    public async Task<PlatformEmailOutboxMessage?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
        => await _db.PlatformEmailOutbox.FindAsync(new object[] { id }, ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.PlatformEmailOutbox.FindAsync(new object[] { id }, ct);
        if (row is null) return;
        _db.PlatformEmailOutbox.Remove(row);
        await _db.SaveChangesAsync(ct);
    }
}
