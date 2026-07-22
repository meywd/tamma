using Microsoft.EntityFrameworkCore;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF-backed <see cref="IChannelOutboxRepository"/>. Strictly tenant-scoped: every
/// operation routes through <see cref="ITenantDbContextFactory"/> so the read/write
/// hits the per-tenant DB, and carries an explicit <c>TenantId</c> predicate
/// (defence-in-depth for the shared-DB phase; the per-tenant schema is the real
/// isolation plane). Mirrors <see cref="EmailOutboxRepository"/>.
/// </summary>
public class ChannelOutboxRepository(
    ITenantDbContextFactory tenantDbFactory,
    ControlPlaneDbContext cp) : IChannelOutboxRepository
{
    public async Task<ChannelOutboxMessage> EnqueueAsync(ChannelOutboxMessage msg, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(msg);
        if (msg.TenantId == Guid.Empty)
            throw new ArgumentException("ChannelOutboxRepository requires a non-empty TenantId.", nameof(msg));
        if (msg.Id == Guid.Empty)
            throw new ArgumentException("ChannelOutboxMessage.Id (the envelope message id) must be set by the caller.", nameof(msg));

        var now = DateTime.UtcNow;
        msg.Status = "pending";
        msg.Attempts = 0;
        msg.DeliveredAt = null;
        msg.AckedAt = null;
        if (msg.CreatedAt == default) msg.CreatedAt = now;

        await using var db = await tenantDbFactory.CreateAsync(msg.TenantId, ct);
        db.ChannelOutbox.Add(msg);
        await db.SaveChangesAsync(ct);
        return msg;
    }

    public async Task<List<ChannelOutboxMessage>> ListUnackedAsync(
        Guid tenantId, string audience, Guid? recipientUserId, int limit, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
        return await db.ChannelOutbox
            .Where(m => m.TenantId == tenantId
                && m.Audience == audience
                && m.RecipientUserId == recipientUserId
                && m.Status != "acked")
            .OrderBy(m => m.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task MarkDeliveredAsync(Guid tenantId, Guid messageId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
        var row = await db.ChannelOutbox
            .Where(m => m.Id == messageId && m.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
        if (row is null || row.Status == "acked") return;

        row.Status = "delivered";
        row.DeliveredAt = DateTime.UtcNow;
        row.Attempts += 1;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> AckAsync(Guid tenantId, Guid messageId, Guid? recipientUserId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
        var row = await db.ChannelOutbox
            .Where(m => m.Id == messageId
                && m.TenantId == tenantId
                && m.RecipientUserId == recipientUserId)
            .FirstOrDefaultAsync(ct);

        // Idempotent: missing row, wrong recipient, or an already-acked row → false.
        if (row is null || row.Status == "acked") return false;

        row.Status = "acked";
        row.AckedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<ChannelOutboxMessage>> ListStaleAsync(
        Guid tenantId, DateTime staleBefore, int limit, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));

        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
        return await db.ChannelOutbox
            .Where(m => m.TenantId == tenantId
                && m.Status != "acked"
                && (m.Status == "pending"
                    || (m.DeliveredAt != null && m.DeliveredAt < staleBefore)))
            .OrderBy(m => m.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> ListTenantsWithPendingAsync(CancellationToken ct = default)
    {
        // Snapshot the active tenant set up-front (cheap single SELECT bounded by
        // the tenants table), then keep the ones with at least one unacked row.
        // Mirrors EmailOutboxRepository.ClaimNextPendingFromAnyTenantAsync's fan-out.
        var activeTenantIds = await cp.Tenants
            .AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(ct);

        var withPending = new List<Guid>();
        foreach (var tid in activeTenantIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await using var db = await tenantDbFactory.CreateAsync(tid, ct);
                var any = await db.ChannelOutbox
                    .AnyAsync(m => m.TenantId == tid && m.Status != "acked", ct);
                if (any) withPending.Add(tid);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A tenant mid-deletion / transient connection failure must not
                // starve the rest of the drain pass; the next cycle retries.
                continue;
            }
        }

        return withPending;
    }
}
