using Tamma.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Tenant-scoped event store. Writes always go through the tenant factory;
/// reads scope to the ambient tenant.
///
/// <para>Cross-tenant admin queries (<c>QueryAsync(tenantId: null)</c>) are
/// routed per Story 28-1 Decision #2 (see
/// <c>.dev/decisions/story-28-1-design-calls.md</c>):</para>
/// <list type="bullet">
///   <item><b>Platform-lifecycle events</b> (TENANT.PROVISIONED.SUCCESS,
///     EMAIL.QUEUED.SUCCESS, INSTALLATION.CREATED.SUCCESS, etc. — events
///     emitted with <c>TenantId = null</c>) are read from
///     <see cref="IPlatformEventRepository"/> (CP-resident
///     <c>platform_events</c> table) and projected back into
///     <see cref="DomainEvent"/> shape so existing callers stay
///     unchanged.</item>
///   <item><b>Cross-tenant tenant-scoped event search</b> (admin "show me
///     all events across all tenants") is <b>not implemented</b> — it
///     would need a per-tenant fan-out via
///     <see cref="ITenantDbContextFactory"/> driven off the LRU pool's
///     known-warm tenants. No current user story demands it; build when
///     one does. Callers that pass <c>type == null</c> AND
///     <c>tenantId == null</c> get a <see cref="NotSupportedException"/>
///     — they almost certainly meant a platform-lifecycle scan and the
///     missing type prefix is the bug.</item>
/// </list>
///
/// <para>During the Story 28-1 transitional shared-DB phase the legacy
/// <see cref="ControlPlaneDbContext.DomainEvents"/> DbSet still carries
/// platform-scope rows (<c>TenantId == null</c>) that were appended via
/// the pre-PR-D code path. We UNION <em>only those</em> rows with
/// <c>platform_events</c> on tenant-less reads — the legacy half is
/// explicitly filtered to <c>TenantId == null</c> so tenant-scoped rows
/// that share the physical table during transition do not bleed into
/// cross-tenant admin views. Once PR D drops <c>cp.domain_events</c>
/// the union side becomes a no-op (the table no longer exists in the
/// model).</para>
/// </summary>
public class EventRepository(
    ITenantDbContextFactory tenantDbFactory,
    ITenantContext tenantContext,
    IPlatformEventRepository? platformEvents = null) : IEventRepository
{
    public async Task<DomainEvent> AppendAsync(DomainEvent evt)
    {
        evt.CreatedAt = DateTime.UtcNow;

        var tid = evt.TenantId ?? tenantContext.TenantId;
        if (tid is null)
        {
            // Story 28-1 PR D — platform-scope (null tenant) events live on
            // platform_events. When IPlatformEventRepository is wired we
            // transparently delegate; older callers that don't yet know
            // about the split keep working. Without a platform repo the
            // append is a hard error so the missing wiring surfaces loudly.
            if (platformEvents is null)
            {
                throw new InvalidOperationException(
                    "EventRepository.AppendAsync requires a tenant id. " +
                    "Story 28-1 PR D moved domain_events off the control plane; " +
                    "platform-scope events (TenantId == null) must be appended " +
                    "via IPlatformEventRepository.AppendAsync instead.");
            }
            await platformEvents.AppendAsync(new PlatformEvent
            {
                Id = evt.Id == Guid.Empty ? Guid.NewGuid() : evt.Id,
                Type = evt.Type,
                TenantId = null,
                Tags = evt.Tags,
                Metadata = evt.Metadata,
                Data = evt.Data,
                CreatedAt = evt.CreatedAt,
            });
            return evt;
        }

        evt.TenantId = tid;
        await using var db = await tenantDbFactory.CreateAsync(tid.Value);

        // Idempotent append on the stable per-event Id. The engine's at-least-
        // once drain re-sends the entire pending slice on any non-2xx, so the
        // events that DID persist in a partially-failed batch arrive again on
        // retry. Without dedup that produces duplicate audit rows (C2). When
        // the caller supplies a non-empty Id we treat a re-send as a no-op:
        // a cheap pre-check keeps the common path off the exception machinery,
        // and the PRIMARY KEY closes the concurrent-insert race below. An empty
        // Id keeps the legacy behaviour (server gen_random_uuid() default).
        if (evt.Id != Guid.Empty)
        {
            var exists = await db.DomainEvents.AsNoTracking()
                .IgnoreQueryFilters()
                .AnyAsync(e => e.Id == evt.Id);
            if (exists) return evt;
        }

        db.DomainEvents.Add(evt);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (evt.Id != Guid.Empty && IsUniqueViolation(ex))
        {
            // A concurrent re-send won the race — the row already exists. Detach
            // the rejected entry and treat the append as the no-op it is.
            db.Entry(evt).State = EntityState.Detached;
        }
        return evt;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg &&
        pg.SqlState == PostgresErrorCodes.UniqueViolation;

    public async Task<DomainEvent?> GetByIdAsync(Guid id)
    {
        var tid = tenantContext.TenantId
            ?? throw new InvalidOperationException(
                "EventRepository.GetByIdAsync requires an ambient tenant id. " +
                "Story 28-1 PR D moved domain_events off the control plane; " +
                "cross-tenant lookup by id is not implemented (Decision #2).");
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await db.DomainEvents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == id);
    }

    public async Task<List<DomainEvent>> QueryAsync(
        Guid? tenantId, string? type, int? issueNumber, int limit)
    {
        if (tenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            var query = db.DomainEvents.Where(e => e.TenantId == tid);
            if (!string.IsNullOrEmpty(type))
                query = query.Where(e => e.Type == type);
            if (issueNumber.HasValue)
                query = query.Where(e => e.IssueNumber == issueNumber.Value);
            return await query.OrderByDescending(e => e.CreatedAt).Take(limit).ToListAsync();
        }

        // Tenant-less query — Story 28-1 Decision #2 (cross-tenant admin
        // queries get a per-call answer). The supported answer here is
        // platform-lifecycle events: read from platform_events and project
        // back into DomainEvent shape so existing callers stay unchanged.
        if (issueNumber.HasValue)
        {
            throw new NotSupportedException(
                "Cross-tenant tenant-scoped event search is not implemented. " +
                "`issueNumber` is a tenant-scoped predicate; pass a `tenantId` " +
                "to scope to one tenant, or drop the issueNumber filter to " +
                "query platform-lifecycle events. See " +
                ".dev/decisions/story-28-1-design-calls.md Decision #2 for " +
                "the per-call routing matrix.");
        }

        // Story 28-1 PR D: cp.DomainEvents is gone. Tenant-less reads
        // resolve to platform_events only. Callers that need cross-tenant
        // tenant-scoped events should fan out via ITenantDbContextFactory
        // when a user story demands it.
        var platformRows = platformEvents is not null
            ? await platformEvents.QueryAsync(typePrefix: type, limit: limit)
            : (IReadOnlyList<PlatformEvent>)Array.Empty<PlatformEvent>();

        return platformRows
            .Select(p => new DomainEvent
            {
                Id = p.Id,
                Type = p.Type,
                TenantId = p.TenantId,
                Tags = p.Tags,
                Metadata = p.Metadata,
                Data = p.Data,
                CreatedAt = p.CreatedAt,
                SequenceNumber = p.SequenceNumber,
                IssueNumber = null,
            })
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit)
            .ToList();
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

    public async Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
        Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
    {
        // Tenant-scoped path — mirrors QueryAsync's exact-match semantics
        // and adds offset + Total. Cross-tenant (null tenantId) is not
        // supported here for the same reason QueryAsync rejects it with an
        // issueNumber: paginated cross-tenant scans would need a per-tenant
        // fan-out via ITenantDbContextFactory and no story demands it.
        if (tenantId is not Guid tid)
        {
            throw new NotSupportedException(
                "QueryWithPaginationAsync requires a tenant id. Cross-tenant " +
                "paginated event search would need a per-tenant fan-out via " +
                "ITenantDbContextFactory; no current story demands it. See " +
                ".dev/decisions/story-28-1-design-calls.md Decision #2.");
        }

        await using var db = await tenantDbFactory.CreateAsync(tid);
        var query = db.DomainEvents.Where(e => e.TenantId == tid);
        if (!string.IsNullOrEmpty(type))
            query = query.Where(e => e.Type == type);
        if (issueNumber.HasValue)
            query = query.Where(e => e.IssueNumber == issueNumber.Value);

        var total = await query.CountAsync();
        var rows = await query
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.SequenceNumber)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();

        return (rows, total);
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
