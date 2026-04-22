using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Tenant-scoped event store. Writes always go through the tenant factory;
/// reads scope to the ambient tenant. Cross-tenant admin queries
/// (<c>QueryAsync(tenantId: null)</c>) use <see cref="ControlPlaneDbContext"/>
/// because the factory requires a specific tenant.
/// </summary>
public class EventRepository(
    ITenantDbContextFactory tenantDbFactory,
    ITenantContext tenantContext,
    ControlPlaneDbContext cp) : IEventRepository
{
    public async Task<DomainEvent> AppendAsync(DomainEvent evt)
    {
        evt.CreatedAt = DateTime.UtcNow;

        // Platform-scope events (no tenant) — e.g. Resend email without a
        // tenant-bound sender — write to CP. Tenant-scoped events route
        // through the factory.
        var tid = evt.TenantId ?? tenantContext.TenantId;
        if (tid is null)
        {
            cp.DomainEvents.Add(evt);
            await cp.SaveChangesAsync();
            return evt;
        }

        evt.TenantId = tid;
        await using var db = await tenantDbFactory.CreateAsync(tid.Value);
        db.DomainEvents.Add(evt);
        await db.SaveChangesAsync();
        return evt;
    }

    public async Task<DomainEvent?> GetByIdAsync(Guid id)
    {
        if (tenantContext.TenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            return await db.DomainEvents.IgnoreQueryFilters()
                .FirstOrDefaultAsync(e => e.Id == id);
        }
        return await cp.DomainEvents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == id);
    }

    public async Task<List<DomainEvent>> QueryAsync(
        Guid? tenantId, string? type, int? issueNumber, int limit)
    {
        if (tenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            // Explicit tenant predicate — the factory-issued context no
            // longer carries an EF query filter (the Npgsql per-tenant
            // connection is the real isolation plane; during transition
            // the physical DB is shared so we filter at query time).
            var query = db.DomainEvents.Where(e => e.TenantId == tid);
            if (!string.IsNullOrEmpty(type))
                query = query.Where(e => e.Type == type);
            if (issueNumber.HasValue)
                query = query.Where(e => e.IssueNumber == issueNumber.Value);
            return await query.OrderByDescending(e => e.CreatedAt).Take(limit).ToListAsync();
        }

        // Cross-tenant admin view (system scope). CP context exposes all rows.
        var q = cp.DomainEvents.IgnoreQueryFilters().AsQueryable();
        if (!string.IsNullOrEmpty(type))
            q = q.Where(e => e.Type == type);
        if (issueNumber.HasValue)
            q = q.Where(e => e.IssueNumber == issueNumber.Value);
        return await q.OrderByDescending(e => e.CreatedAt).Take(limit).ToListAsync();
    }

    public async Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
    {
        await using var db = await tenantDbFactory.CreateAsync(tenantId);
        return await db.DomainEvents
            .Where(e => e.TenantId == tenantId && e.Type == type)
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task ClearAsync(Guid tenantId)
    {
        await using var db = await tenantDbFactory.CreateAsync(tenantId);
        // IgnoreQueryFilters() is safe here because the context is already
        // fixed to this tenant — there is no other tenant's data to reach.
        var events = await db.DomainEvents.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId).ToListAsync();
        db.DomainEvents.RemoveRange(events);
        await db.SaveChangesAsync();
    }

    public async Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
        Guid tenantId, string? typePrefix, int limit, int offset)
    {
        // Factory-issued tenant context carries no EF query filter — the
        // per-tenant Npgsql connection is the real isolation plane. The
        // explicit TenantId predicate is defence-in-depth for the
        // transitional shared-DB phase where multiple tenants may still
        // share a physical database.
        await using var db = await tenantDbFactory.CreateAsync(tenantId);
        var query = db.DomainEvents
            .Where(e => e.TenantId == tenantId);

        if (!string.IsNullOrEmpty(typePrefix))
        {
            // EF.Functions.Like translates to SQL LIKE on Postgres which is
            // index-friendly for prefix matches.
            var like = typePrefix + "%";
            query = query.Where(e => EF.Functions.Like(e.Type, like));
        }

        var total = await query.CountAsync();
        var rows = await query
            .OrderByDescending(e => e.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();

        return (rows, total);
    }
}
