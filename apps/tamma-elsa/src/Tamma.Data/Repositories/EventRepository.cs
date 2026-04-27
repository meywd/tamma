using Tamma.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
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
    ControlPlaneDbContext cp,
    IPlatformEventRepository? platformEvents = null) : IEventRepository
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

        // Tenant-less query — Story 28-1 Decision #2 (cross-tenant admin
        // queries get a per-call answer). The supported answer here is
        // platform-lifecycle events: read from platform_events and project
        // back into DomainEvent shape so existing callers stay unchanged.
        //
        // A non-null `issueNumber` is meaningless for platform-scope events
        // (those rows have no IssueNumber column) — that combination is
        // almost certainly a per-tenant query someone forgot to scope, so
        // we reject it loudly per Decision #2's "build when a story
        // demands it" rule rather than silently returning no rows.
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

        // Read the platform_events log (CP-resident; survives PR D).
        // typePrefix matches DomainEvent's exact-type semantics for full
        // event type strings (e.g. "EMAIL.QUEUED.SUCCESS") because no
        // other event type starts with that string. A null/empty type
        // returns every platform-scope event, capped at `limit`.
        // PR-C/PR-B-fix: platformEvents is optional. When the platform
        // repo isn't registered (some test scopes deliberately exclude
        // it to verify graceful degradation), skip the platform-events
        // half of the union and return only the legacy CP rows. Once
        // PR D drops cp.DomainEvents, callers without IPlatformEventRepository
        // simply get an empty result rather than a DI activation failure.
        var platformRows = platformEvents is not null
            ? await platformEvents.QueryAsync(typePrefix: type, limit: limit)
            : (IReadOnlyList<PlatformEvent>)Array.Empty<PlatformEvent>();

        // Transitional UNION: the pre-Story-28-1 code path appended
        // tenant-less events to cp.DomainEvents. Until PR D drops the
        // DbSet those rows must still be visible.
        //
        // SECURITY: scope the legacy half to TenantId == null. During the
        // transitional shared-DB phase, the physical cp.domain_events
        // table mixes rows from every tenant — a tenant-less query with
        // a tenant-scoped event type (e.g. CODE.GENERATED.SUCCESS) would
        // otherwise leak rows from every tenant into the cross-tenant
        // admin view. Per Decision #2 the supported answer here is
        // platform-lifecycle events only; the issueNumber guard above
        // catches one signal of tenant-scoped intent but a caller can
        // still pass a tenant-scoped `type` with no `issueNumber`. The
        // TenantId-null predicate makes the leak structurally
        // impossible regardless of what `type` carries. Once PR D drops
        // cp.DomainEvents this branch becomes a no-op.
        //
        // Type-predicate semantics: the platform half uses prefix-LIKE
        // (`type%`) via IPlatformEventRepository.QueryAsync. The legacy
        // half mirrors that here so the two routing branches return the
        // same row shape for the same `type` argument. Reviewer note
        // (#340 LOW): full event type strings like "EMAIL.QUEUED.SUCCESS"
        // are only a prefix of themselves, so prefix-LIKE doesn't change
        // behaviour for full strings; it lets short prefixes ("EMAIL")
        // work consistently across both halves.
        var legacy = cp.DomainEvents
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == null)
            .AsQueryable();
        if (!string.IsNullOrEmpty(type))
        {
            var like = type + "%";
            legacy = legacy.Where(e => EF.Functions.Like(e.Type, like));
        }
        if (issueNumber.HasValue)
            legacy = legacy.Where(e => e.IssueNumber == issueNumber.Value);
        var legacyRows = await legacy
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit)
            .ToListAsync();

        // Merge both streams, newest-first, capped at `limit`. The
        // PlatformEvent → DomainEvent projection drops the UserId column
        // (DomainEvent has no equivalent slot) and synthesises a missing
        // IssueNumber — callers already tolerate null IssueNumber on
        // platform-scope events.
        var merged = legacyRows
            .Concat(platformRows.Select(p => new DomainEvent
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
            }))
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit)
            .ToList();

        return merged;
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
