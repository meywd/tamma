using Tamma.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF-backed <see cref="IEmailOutboxRepository"/>. Strictly tenant-scoped:
/// every operation routes through <see cref="ITenantDbContextFactory"/>
/// so the read/write hits the per-tenant DB. Platform-scope email
/// (verification, password reset, welcome) goes through
/// <see cref="IPlatformEmailOutboxRepository"/> instead — see
/// <c>.dev/decisions/story-28-1-design-calls.md</c> §5 for the matrix.
///
/// <para>During the Story 28-1 transition the per-tenant
/// <c>email_outbox</c> table physically still co-resides on the CP DB —
/// PR D moves it to per-tenant DBs. Until then a tenant-bound DB
/// context built by the factory still sees rows that any other
/// tenant-bound context with the same connection sees. The split here
/// is logical so the eventual physical move is a no-op for callers.</para>
/// </summary>
public class EmailOutboxRepository(
    ITenantDbContextFactory tenantDbFactory,
    ControlPlaneDbContext cp) : IEmailOutboxRepository
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<EmailOutboxMessage> EnqueueAsync(
        EmailOutboxMessage msg, CancellationToken ct = default)
    {
        if (msg.TenantId is not Guid tid || tid == Guid.Empty)
        {
            throw new ArgumentException(
                "EmailOutboxRepository requires a non-empty TenantId. " +
                "Platform-scope email (verification, password reset, " +
                "welcome) must go through IPlatformEmailOutboxRepository.",
                nameof(msg));
        }

        var now = DateTime.UtcNow;
        msg.Status = "pending";
        msg.Attempts = 0;
        msg.LastError = null;
        msg.SentAt = null;
        if (msg.NextAttemptAt == default) msg.NextAttemptAt = now;
        msg.CreatedAt = now;
        msg.UpdatedAt = now;
        if (msg.MaxAttempts <= 0) msg.MaxAttempts = 5;

        await using var db = await tenantDbFactory.CreateAsync(tid, ct);
        db.EmailOutbox.Add(msg);
        await db.SaveChangesAsync(ct);
        return msg;
    }

    public async Task<EmailOutboxMessage?> ClaimNextPendingAsync(
        Guid tenantId, DateTime now, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);

        if (string.Equals(db.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal))
        {
            return await ClaimViaPostgresAsync(db, tenantId, now, ct);
        }
        return await ClaimViaNaivePathAsync(db, tenantId, now, ct);
    }

    public async Task<EmailOutboxMessage?> ClaimNextPendingFromAnyTenantAsync(
        DateTime now, CancellationToken ct = default)
    {
        // Snapshot the active tenant set up-front. Cheap (single SELECT
        // bounded by the tenants table size) relative to the per-tenant
        // SMTP delivery this drain pass precedes.
        //
        // Story 28-1 PR B: replaces the previous "scan a single shared
        // CP table" path with a per-tenant fan-out. Returns the FIRST
        // tenant that yields a claimable row; the next poll cycle picks
        // up where this one left off. Doesn't try to be globally
        // FIFO across tenants — within a tenant FIFO is preserved by
        // ClaimNextPendingAsync's ORDER BY clause.
        var activeTenantIds = await cp.Tenants
            .AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .OrderBy(t => t.Id) // deterministic-but-cheap traversal order
            .Select(t => t.Id)
            .ToListAsync(ct);

        foreach (var tid in activeTenantIds)
        {
            if (ct.IsCancellationRequested) return null;

            EmailOutboxMessage? row;
            try
            {
                row = await ClaimNextPendingAsync(tid, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Tenant might be mid-deletion or have a transient
                // connection failure; don't let one tenant's outage
                // starve the rest. The caller (OutboxSmtpSender) logs
                // and the next poll re-tries. Wave-4 review M2 —
                // cooperative cancellation must propagate, not be
                // swallowed as a "tenant outage".
                continue;
            }

            if (row is not null) return row;
        }

        return null;
    }

    private static async Task<EmailOutboxMessage?> ClaimViaPostgresAsync(
        TenantDbContext db, Guid tenantId, DateTime now, CancellationToken ct)
    {
        // Story 28-1 PR B fix (wave-4 review H1) — `WHERE "TenantId" = @tid`
        // is REQUIRED on both the inner SELECT and the outer UPDATE while the
        // per-tenant `email_outbox` table physically still co-resides on the
        // CP DB. Without it tenant A's call would claim the globally oldest
        // pending row regardless of which tenant owns it.
        var rows = await db.EmailOutbox.FromSqlInterpolated($"""
            UPDATE email_outbox
            SET "Status" = 'sending', "UpdatedAt" = {now}
            WHERE "Id" = (
                SELECT "Id" FROM email_outbox
                WHERE "Status" = 'pending'
                  AND "NextAttemptAt" <= {now}
                  AND "TenantId" = {tenantId}
                ORDER BY "NextAttemptAt" ASC, "CreatedAt" ASC
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
              AND "TenantId" = {tenantId}
            RETURNING *
            """).AsNoTracking().ToListAsync(ct);

        return rows.FirstOrDefault();
    }

    private static async Task<EmailOutboxMessage?> ClaimViaNaivePathAsync(
        TenantDbContext db, Guid tenantId, DateTime now, CancellationToken ct)
    {
        // Story 28-1 PR B fix (wave-4 review H1) — `&& m.TenantId == tenantId`
        // is REQUIRED in the predicate while the per-tenant table still
        // physically co-resides on the CP DB. EF-InMemory hides the missing
        // predicate; the cross-tenant negative tests catch it.
        var candidate = await db.EmailOutbox
            .Where(m => m.Status == "pending"
                && m.NextAttemptAt <= now
                && m.TenantId == tenantId)
            .OrderBy(m => m.NextAttemptAt)
            .ThenBy(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (candidate is null) return null;

        candidate.Status = "sending";
        candidate.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return candidate;
    }

    public async Task MarkSentAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
        // Story 28-1 PR B fix (wave-4 review H1) — FindAsync keys on PK
        // only. While the per-tenant table physically co-resides on the
        // CP DB, tenant A could mark tenant B's row sent. Predicate-based
        // lookup makes the tenant id a hard guard.
        var msg = await db.EmailOutbox
            .Where(m => m.Id == id && m.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
        if (msg is null) return;

        var now = DateTime.UtcNow;
        msg.Status = "sent";
        msg.LastError = null;
        msg.SentAt = now;
        msg.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task<EmailOutboxMessage?> MarkFailedAsync(
        Guid tenantId, Guid id, string error, TimeSpan? backoff, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
        // Story 28-1 PR B fix (wave-4 review H1) — see MarkSentAsync.
        var msg = await db.EmailOutbox
            .Where(m => m.Id == id && m.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
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

        await db.SaveChangesAsync(ct);
        return msg;
    }

    public async Task<EmailOutboxMessage?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
        // Story 28-1 PR B fix (wave-4 review H1) — see MarkSentAsync.
        return await db.EmailOutbox
            .Where(m => m.Id == id && m.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
        // Story 28-1 PR B fix (wave-4 review H1) — see MarkSentAsync.
        var row = await db.EmailOutbox
            .Where(m => m.Id == id && m.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
        if (row is null) return;
        db.EmailOutbox.Remove(row);
        await db.SaveChangesAsync(ct);
    }
}
