using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF-backed event store. Uses the global query filter on
/// <see cref="DomainEvent"/> (see <see cref="TammaDbContext.OnModelCreating"/>)
/// which scopes reads to the ambient <see cref="ITenantContext"/>.
///
/// <para>Audit finding 028 (RLS bypass): the previous version called
/// <c>IgnoreQueryFilters()</c> on every query, defeating the global tenant
/// filter and effectively making cross-tenant access the default. This
/// version honours the global filter; cross-tenant call sites that legitimately
/// need it (e.g. <see cref="GetLastByTypeAsync"/> when called from system
/// scope, the dashboard "all-tenants" admin view) must opt in via the
/// <c>*CrossTenant</c> overloads.</para>
///
/// <para>Postgres RLS itself is dormant pending the Phase-3 connection-string
/// split; this repo enforces tenant scoping at the EF layer in the meantime.</para>
/// </summary>
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
        // Honour the global query filter — when the ambient tenant context is
        // set, it scopes; when it's null (system scope, e.g. background
        // processor), the filter degrades to "all tenants". Caller-supplied
        // tenantId narrows further inside that.
        var query = db.DomainEvents.AsQueryable();
        if (tenantId.HasValue)
            query = query.Where(e => e.TenantId == tenantId.Value);
        if (!string.IsNullOrEmpty(type))
            query = query.Where(e => e.Type == type);
        if (issueNumber.HasValue)
            query = query.Where(e => e.IssueNumber == issueNumber.Value);
        return await query.OrderByDescending(e => e.CreatedAt).Take(limit).ToListAsync();
    }

    public async Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
        => await db.DomainEvents
            .Where(e => e.TenantId == tenantId && e.Type == type)
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync();

    public async Task ClearAsync(Guid tenantId)
    {
        // ClearAsync is an admin / test helper; bypass the ambient filter so
        // the explicit tenantId argument is the sole authority. Without
        // IgnoreQueryFilters() here, a clear request from a different ambient
        // tenant would silently delete nothing.
        var events = await db.DomainEvents.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId).ToListAsync();
        db.DomainEvents.RemoveRange(events);
        await db.SaveChangesAsync();
    }

    public async Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
        Guid tenantId, string? typePrefix, int limit, int offset)
    {
        // Honour the global query filter first (defence-in-depth: if the
        // caller forgot to set ambient tenant we still get cross-tenant
        // rejection from the explicit Where below). Both the Where +
        // ambient filter combine to require TenantId == tenantId.
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
