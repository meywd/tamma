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

    /// <inheritdoc />
    public async Task<(IReadOnlyList<DomainEvent> Events, int? Total)> QueryAgentTrailAsync(
        Guid tenantId, Guid agentId, string? typePrefix,
        DateTimeOffset? from, DateTimeOffset? to,
        string? role, string? provider, string? outcome,
        long? cursor, int limit, bool includeTotal = false)
    {
        // Story 32-6 AC4 — the hard tenant guard. An empty tenant is not a
        // cross-tenant scan we implement; it is a bug. Mirror the
        // QueryWithPaginationAsync guard so a cross-tenant trail read is
        // unimplementable through this repository, not merely unauthorized.
        if (tenantId == Guid.Empty)
        {
            throw new NotSupportedException(
                "QueryAgentTrailAsync requires a non-empty tenant id. The per-agent " +
                "action trail (Story 32-6) is ALWAYS tenant-scoped — there is no " +
                "cross-tenant or platform-admin read path. See " +
                ".dev/decisions/story-28-1-design-calls.md Decision #2.");
        }

        await using var db = await tenantDbFactory.CreateAsync(tenantId);

        // The agentId + role + provider predicates live in the Tags JSONB. They
        // are expressed as raw SQL `->>` extractions (the column is jsonb) so they
        // translate on Postgres and use the JSONB values written by
        // AgentTrailTags.Build. role/provider are optional via the
        // `({param} IS NULL OR ...)` idiom. The unqualified `domain_events` name
        // resolves to the tenant schema via the per-tenant connection's
        // search_path. Non-JSONB filters (tenant defence-in-depth, type prefix,
        // outcome→type, date, cursor, order, take) compose in LINQ over the
        // raw SQL subquery.
        //
        // The `"Tags"->>'agentId' = $1` equality is served by the btree EXPRESSION
        // index `ix_domain_events_tags_agentid` on `((Tags->>'agentId'))` (added in
        // migration AddAgentTrailAgentIdIndex) — WITHOUT it this is a seq scan of the
        // whole 100%-audit stream. The expression matches the predicate exactly, so
        // Postgres uses the index for this lookup.
        var agentIdText = agentId.ToString();
        // The `::text` casts pin the parameter type so Postgres does not reject
        // the `$n IS NULL` predicate with "could not determine data type of
        // parameter" when role/provider are omitted (NULL).
        IQueryable<DomainEvent> query = db.DomainEvents
            .FromSqlInterpolated($@"
                SELECT * FROM domain_events
                WHERE ""Tags""->>'agentId' = {agentIdText}
                  AND ({role}::text IS NULL OR ""Tags""->>'role' = {role})
                  AND ({provider}::text IS NULL OR ""Tags""->>'provider' = {provider})");

        // Defence-in-depth tenant predicate (structural isolation is the
        // per-tenant connection; this keeps the slice tight during the
        // transitional shared-DB phase).
        query = query.Where(e => e.TenantId == tenantId);

        if (!string.IsNullOrEmpty(typePrefix))
        {
            var like = typePrefix + "%";
            query = query.Where(e => EF.Functions.Like(e.Type, like));
        }

        var outcomeType = MapOutcomeToType(outcome);
        if (outcomeType is not null)
        {
            query = query.Where(e => e.Type == outcomeType);
        }

        if (from is { } f)
        {
            var fromUtc = f.UtcDateTime;
            query = query.Where(e => e.CreatedAt >= fromUtc);
        }
        if (to is { } t)
        {
            var toUtc = t.UtcDateTime;
            query = query.Where(e => e.CreatedAt < toUtc);
        }

        // Story 32-6 (review I2) — the total is an UNBOUNDED COUNT(*) over the
        // tenant's 100%-audit stream; running it on every page is wasteful.
        // Compute it ONLY when the caller opts in; otherwise pagination relies on
        // hasMore/nextCursor and Total stays null ("not computed", not "zero").
        int? total = includeTotal ? await query.CountAsync() : null;

        if (cursor is { } c)
        {
            // SequenceNumber DESC page: everything strictly older than the last
            // sequence number the caller saw. Immune to same-millisecond
            // CreatedAt collisions (AC5).
            query = query.Where(e => e.SequenceNumber < c);
        }

        var rows = await query
            .OrderByDescending(e => e.SequenceNumber)
            .Take(limit)
            .ToListAsync();

        return (rows, total);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByCorrelationIdAsync(Guid tenantId, string correlationId)
    {
        if (string.IsNullOrEmpty(correlationId))
        {
            return false;
        }

        await using var db = await tenantDbFactory.CreateAsync(tenantId);

        // The correlationId predicate lives in the Tags JSONB. It is a PARAMETERIZED
        // raw-SQL `->>` extraction (the column is jsonb) so it translates on Postgres
        // against the values written into Tags — {correlationId} is bound as a
        // parameter, never interpolated into the SQL text. The unqualified
        // `domain_events` name resolves to the tenant schema via the per-tenant
        // connection's search_path. The tenant defence-in-depth predicate + the
        // EXISTS (from AnyAsync) compose in LINQ over the raw-SQL subquery.
        //
        // The `"Tags"->>'correlationId' = $1` equality is served by the btree
        // EXPRESSION index `ix_domain_events_tags_correlationid` on
        // ((Tags->>'correlationId')) (added in migration AddDomainEventsCorrelationIdIndex,
        // mirroring ix_domain_events_tags_agentid on ((Tags->>'agentId')) — see
        // AddAgentTrailAgentIdIndex). AnyAsync also compiles to EXISTS(SELECT 1 …) so
        // Postgres short-circuits on the first match — the lookup is volume-independent
        // regardless, unlike the retired recent-200 scan.
        return await db.DomainEvents
            .FromSqlInterpolated($@"
                SELECT * FROM domain_events
                WHERE ""Tags""->>'correlationId' = {correlationId}")
            .Where(e => e.TenantId == tenantId)
            .AnyAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DomainEvent>> ListByCorrelationIdAsync(
        Guid tenantId, string correlationId)
    {
        if (string.IsNullOrEmpty(correlationId))
        {
            return Array.Empty<DomainEvent>();
        }

        await using var db = await tenantDbFactory.CreateAsync(tenantId);

        // Same tenant-scoped PARAMETERIZED `->>` lookup as ExistsByCorrelationIdAsync,
        // but returns EVERY event for the run (NOT capped) ordered oldest-first for
        // replay. Volume-independent: the target run's events are returned regardless
        // of how many other AGENT.* events the tenant has.
        //
        // The `"Tags"->>'correlationId' = $1` equality is served by the btree
        // EXPRESSION index `ix_domain_events_tags_correlationid` on
        // ((Tags->>'correlationId')) (migration AddDomainEventsCorrelationIdIndex; see
        // ExistsByCorrelationIdAsync / AddAgentTrailAgentIdIndex).
        var rows = await db.DomainEvents
            .FromSqlInterpolated($@"
                SELECT * FROM domain_events
                WHERE ""Tags""->>'correlationId' = {correlationId}")
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync();

        return rows;
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<DomainEvent> Events, int? Total)> QueryEventsAsync(
        Guid tenantId,
        string? type, bool typeIsPrefix,
        string? correlationId,
        string? actor,
        DateTimeOffset? from, DateTimeOffset? to,
        long? cursor, int limit, bool includeTotal = false)
    {
        // Story 4-7 — the hard tenant guard. An empty tenant is not a cross-tenant
        // scan we implement; it is a bug. Mirror QueryAgentTrailAsync /
        // QueryWithPaginationAsync so a cross-tenant read is unimplementable through
        // this repository, not merely unauthorized. The endpoint returns an empty
        // page for a missing tenant BEFORE reaching here, so this guard only fires
        // on an internal caller that forgot to scope.
        if (tenantId == Guid.Empty)
        {
            throw new NotSupportedException(
                "QueryEventsAsync requires a non-empty tenant id. The time-travel " +
                "event query (Story 4-7) is ALWAYS tenant-scoped — there is no " +
                "cross-tenant or platform-admin read path. See " +
                ".dev/decisions/story-28-1-design-calls.md Decision #2.");
        }

        await using var db = await tenantDbFactory.CreateAsync(tenantId);

        // The correlationId + actor(userId) predicates live in the Tags JSONB. They
        // are PARAMETERIZED raw-SQL `->>` extractions (the column is jsonb) so they
        // translate on Postgres against the values in Tags — every value is BOUND as
        // a parameter, never interpolated into the SQL text. Optional filters use the
        // `({param}::text IS NULL OR ...)` idiom (same as QueryAgentTrailAsync's
        // role/provider). The `::text` casts pin the parameter type so Postgres does
        // not reject the `$n IS NULL` predicate with "could not determine data type of
        // parameter" when a filter is omitted (NULL). The unqualified `domain_events`
        // name resolves to the tenant schema via the per-tenant connection's
        // search_path.
        //
        // When supplied, `"Tags"->>'correlationId' = $1` is served by the btree
        // EXPRESSION index `ix_domain_events_tags_correlationid` and
        // `"Tags"->>'userId' = $2` by `ix_domain_events_tags_userid` (both raw-SQL
        // expression indexes — see the AddDomainEvents*Index migrations). Non-JSONB
        // filters (tenant defence-in-depth, type, time range, cursor, order, take)
        // compose in LINQ over the raw-SQL subquery.
        IQueryable<DomainEvent> query = db.DomainEvents
            .FromSqlInterpolated($@"
                SELECT * FROM domain_events
                WHERE ({correlationId}::text IS NULL OR ""Tags""->>'correlationId' = {correlationId})
                  AND ({actor}::text IS NULL OR ""Tags""->>'userId' = {actor})");

        // Defence-in-depth tenant predicate (structural isolation is the per-tenant
        // connection; this keeps the slice tight during the transitional shared-DB
        // phase where multiple tenants may still share a physical database).
        query = query.Where(e => e.TenantId == tenantId);

        if (!string.IsNullOrEmpty(type))
        {
            if (typeIsPrefix)
            {
                var like = type + "%";
                query = query.Where(e => EF.Functions.Like(e.Type, like));
            }
            else
            {
                query = query.Where(e => e.Type == type);
            }
        }

        // Half-open window [from, to): inclusive lower bound, exclusive upper.
        if (from is { } f)
        {
            var fromUtc = f.UtcDateTime;
            query = query.Where(e => e.CreatedAt >= fromUtc);
        }
        if (to is { } t)
        {
            var toUtc = t.UtcDateTime;
            query = query.Where(e => e.CreatedAt < toUtc);
        }

        // Opt-in total — an UNBOUNDED COUNT(*) over the filtered stream; skipped by
        // default so paging relies on hasMore/nextCursor and Total stays null ("not
        // computed", not "zero"). Counted BEFORE the cursor predicate so it reflects
        // the full match set, not just the remaining tail.
        int? total = includeTotal ? await query.CountAsync() : null;

        if (cursor is { } c)
        {
            // SequenceNumber DESC page: everything strictly older than the last
            // sequence number the caller saw. Immune to same-millisecond CreatedAt
            // collisions.
            query = query.Where(e => e.SequenceNumber < c);
        }

        var rows = await query
            .OrderByDescending(e => e.SequenceNumber)
            .Take(limit)
            .ToListAsync();

        return (rows, total);
    }

    /// <summary>Map the <c>outcome</c> filter (<c>success|failed|partial</c>) to
    /// the terminal <c>AGENT.TASK.*</c> event type. Returns <c>null</c> when no
    /// (or an unrecognized) outcome is supplied — the caller then does not
    /// constrain by outcome.</summary>
    private static string? MapOutcomeToType(string? outcome) =>
        outcome?.Trim().ToLowerInvariant() switch
        {
            "success" => "AGENT.TASK.SUCCESS",
            "failed" => "AGENT.TASK.FAILED",
            "partial" => "AGENT.TASK.PARTIAL",
            _ => null,
        };
}
