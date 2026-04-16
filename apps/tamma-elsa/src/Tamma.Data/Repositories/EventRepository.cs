using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class EventRepository(TammaDbContext db) : IEventRepository
{
    public async Task<DomainEvent> AppendAsync(DomainEvent evt)
    {
        evt.CreatedAt = DateTime.UtcNow;
        db.DomainEvents.Add(evt);
        await db.SaveChangesAsync();
        return evt;
    }

    public async Task<DomainEvent?> GetByIdAsync(Guid id)
        => await db.DomainEvents.FindAsync(id);

    public async Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
    {
        var query = db.DomainEvents.IgnoreQueryFilters().AsQueryable();
        if (tenantId.HasValue)
            query = query.Where(e => e.TenantId == tenantId.Value);
        if (!string.IsNullOrEmpty(type))
            query = query.Where(e => e.Type == type);
        if (issueNumber.HasValue)
            query = query.Where(e => e.IssueNumber == issueNumber.Value);
        return await query.OrderByDescending(e => e.CreatedAt).Take(limit).ToListAsync();
    }

    public async Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
        => await db.DomainEvents.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && e.Type == type)
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync();

    public async Task ClearAsync(Guid tenantId)
    {
        var events = await db.DomainEvents.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId).ToListAsync();
        db.DomainEvents.RemoveRange(events);
        await db.SaveChangesAsync();
    }
}
