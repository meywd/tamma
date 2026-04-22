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
        var tid = evt.TenantId ?? tenantContext.TenantId
            ?? throw new InvalidOperationException(
                "Cannot append a domain event without a tenant id. Set DomainEvent.TenantId or bind ITenantContext.");

        evt.TenantId = tid;
        evt.CreatedAt = DateTime.UtcNow;

        await using var db = await tenantDbFactory.CreateAsync(tid);
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
            var query = db.DomainEvents.AsQueryable();
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
            .Where(e => e.Type == type)
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
}
