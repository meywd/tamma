using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IEventRepository
{
    Task<DomainEvent> AppendAsync(DomainEvent evt);
    Task<DomainEvent?> GetByIdAsync(Guid id);
    Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit);
    Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type);
    Task ClearAsync(Guid tenantId);

    /// <summary>
    /// Story 4-7 (event-query-API for time-travel) — paginated tenant-scoped
    /// event read with exact <paramref name="type"/> match and optional
    /// <paramref name="issueNumber"/> filter. Backs
    /// <c>GET /api/engine/history</c>. Most-recent first; same exact-match
    /// semantics as <see cref="QueryAsync"/> so callers can drop in the
    /// paginated variant without changing filter behaviour.
    ///
    /// <para>Distinct from <see cref="ListByTenantAsync"/> which uses
    /// prefix matching for the tenant-audit endpoint. Returning a Total
    /// count enables <c>hasMore</c> / <c>nextOffset</c> on the wire.</para>
    /// </summary>
    Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
        Guid? tenantId, string? type, int? issueNumber, int limit, int offset);

    /// <summary>
    /// Tenant-scoped audit log read. Returns events whose
    /// <see cref="DomainEvent.TenantId"/> matches <paramref name="tenantId"/>,
    /// most-recent first, with cursor-style pagination + an optional
    /// <paramref name="typePrefix"/> match against <see cref="DomainEvent.Type"/>.
    ///
    /// <para>Backs the tenant-admin audit endpoint added in story 18-7
    /// (<c>GET /api/v1/orgs/{tenantId}/audit</c>). The defence-in-depth
    /// hook is the global query filter: callers that have set the ambient
    /// tenant context get an additional layer of cross-tenant rejection
    /// even if a future bug bypasses the explicit <c>tenantId</c> filter.</para>
    /// </summary>
    /// <param name="tenantId">Tenant to scope to.</param>
    /// <param name="typePrefix">Optional prefix match (case-sensitive)
    /// against <see cref="DomainEvent.Type"/>. <c>"TENANT.MEMBER"</c>
    /// matches every <c>TENANT.MEMBER_*</c> event in one query.</param>
    /// <param name="limit">Page size (1..200).</param>
    /// <param name="offset">Page offset (>= 0).</param>
    Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
        Guid tenantId, string? typePrefix, int limit, int offset);

    /// <summary>
    /// Story 32-6 (AC4/AC5) — the per-agent ACTION TRAIL read. Returns
    /// action-trail events for one <paramref name="agentId"/> within one
    /// <paramref name="tenantId"/>, filtered by an optional
    /// <paramref name="typePrefix"/> (<c>"AGENT.TASK"</c> for the runs list;
    /// all <c>AGENT.*</c>/<c>REVIEW.BUG.*</c> for the full trail) plus optional
    /// <paramref name="from"/>/<paramref name="to"/> date, <paramref name="role"/>,
    /// <paramref name="provider"/>, and <paramref name="outcome"/>
    /// (<c>success</c>|<c>failed</c>|<c>partial</c>) filters.
    ///
    /// <para><b>Tenant isolation is structural (AC4).</b> The read is physically
    /// scoped to the tenant's <c>t_&lt;hex&gt;.domain_events</c> schema via
    /// <c>ITenantDbContextFactory</c>. There is NO cross-tenant read path: an
    /// empty <paramref name="tenantId"/> throws <see cref="NotSupportedException"/>
    /// — the same hard guard <see cref="QueryWithPaginationAsync"/> uses — so a
    /// cross-tenant trail read is not merely unauthorized, it is unimplementable
    /// through this repository.</para>
    ///
    /// <para><b>Cursor.</b> Pages on <see cref="DomainEvent.SequenceNumber"/>
    /// (the server-side <c>BIGSERIAL</c> total order), never <c>CreatedAt</c>
    /// (which has same-millisecond collisions). <paramref name="cursor"/> is the
    /// last <c>SequenceNumber</c> seen; the next page is
    /// <c>SequenceNumber &lt; cursor</c>, most-recent first. Pagination relies on
    /// <c>hasMore</c>/<c>nextCursor</c> — NOT on the total.</para>
    ///
    /// <para><b>Total is opt-in.</b> The exact count is an UNBOUNDED
    /// <c>COUNT(*)</c> over the tenant's <c>domain_events</c> audit stream; running
    /// it on every page is wasteful. It is computed only when
    /// <paramref name="includeTotal"/> is <c>true</c>; otherwise the returned
    /// <c>Total</c> is <c>null</c> (meaning "not computed", NOT "zero").</para>
    ///
    /// <para>Default interface implementation throws — a repository that does not
    /// implement the trail read (e.g. a lightweight test double) is never a
    /// valid trail source. The real <c>EventRepository</c> overrides it.</para>
    /// </summary>
    Task<(IReadOnlyList<DomainEvent> Events, int? Total)> QueryAgentTrailAsync(
        Guid tenantId, Guid agentId, string? typePrefix,
        DateTimeOffset? from, DateTimeOffset? to,
        string? role, string? provider, string? outcome,
        long? cursor, int limit, bool includeTotal = false)
        => throw new NotSupportedException(
            "QueryAgentTrailAsync is not implemented by this IEventRepository. " +
            "The per-agent action trail (Story 32-6) reads only through the " +
            "tenant-scoped EventRepository, which routes via ITenantDbContextFactory.");

    /// <summary>
    /// Story 32-23 (review fix) — tenant-scoped EXISTENCE check for a run by its
    /// <paramref name="correlationId"/> (the workflow-instance id stamped into every
    /// <c>AGENT.*</c> event's <c>Tags.correlationId</c>). Backs the run-tap ownership
    /// guard: <c>true</c> iff at least one tenant-scoped event carries this
    /// correlationId.
    ///
    /// <para><b>Volume-independent</b> — a single targeted <c>EXISTS</c> lookup that
    /// returns the answer regardless of how many <c>AGENT.*</c> events the tenant has.
    /// This replaces the retired recent-N (200) window scan that could age a busy
    /// tenant's live run out of the window and false-404 the OWNER on their own run.</para>
    ///
    /// <para>The read is structurally scoped to the tenant's <c>t_&lt;hex&gt;</c>
    /// schema — there is no cross-tenant read path. Default throws so a repository that
    /// does not implement it is never a valid ownership source; the real
    /// <c>EventRepository</c> overrides it.</para>
    /// </summary>
    Task<bool> ExistsByCorrelationIdAsync(Guid tenantId, string correlationId)
        => throw new NotSupportedException(
            "ExistsByCorrelationIdAsync is not implemented by this IEventRepository. " +
            "The run-tap ownership guard (Story 32-23) reads only through the " +
            "tenant-scoped EventRepository.");

    /// <summary>
    /// Story 32-23 (review fix) — ALL tenant-scoped events carrying
    /// <paramref name="correlationId"/>, ordered oldest-first (by
    /// <see cref="DomainEvent.SequenceNumber"/>) for replay catch-up + early-terminal
    /// detection.
    ///
    /// <para><b>NOT capped</b> — returns every frame for the run regardless of how
    /// many <c>AGENT.*</c> events the tenant has, so <c>?replay=true</c> never
    /// truncates (unlike the retired recent-200 window scan). Tenant-scoped to the
    /// caller's schema; there is no cross-tenant read path. Default throws — see
    /// <see cref="ExistsByCorrelationIdAsync"/>.</para>
    /// </summary>
    Task<IReadOnlyList<DomainEvent>> ListByCorrelationIdAsync(Guid tenantId, string correlationId)
        => throw new NotSupportedException(
            "ListByCorrelationIdAsync is not implemented by this IEventRepository. " +
            "The run-tap replay (Story 32-23) reads only through the tenant-scoped " +
            "EventRepository.");

    /// <summary>
    /// Story 4-7 (event query API for time-travel) — the general tenant-scoped,
    /// keyset-paginated read over the <c>domain_events</c> DCB stream that backs
    /// <c>GET /api/engine/events/query</c>. This is the QUERY/filter surface; the
    /// point-in-time state RECONSTRUCTION (replaying the filtered slice into a
    /// materialized state snapshot) is deferred to Story 4-8 (#191).
    ///
    /// <para><b>Filters</b> (all optional, composed with AND):
    /// <list type="bullet">
    ///   <item><paramref name="type"/> — event type. Exact match by default;
    ///     prefix match (<c>LIKE 'type%'</c>) when <paramref name="typeIsPrefix"/>
    ///     is <c>true</c> (e.g. <c>"AGENT.TASK"</c> matches every
    ///     <c>AGENT.TASK.*</c>).</item>
    ///   <item><paramref name="correlationId"/> — the workflow-instance /
    ///     run correlation id stamped into <c>Tags.correlationId</c> (served by
    ///     <c>ix_domain_events_tags_correlationid</c>).</item>
    ///   <item><paramref name="actor"/> — the acting principal, matched against
    ///     the DCB <c>Tags.userId</c> convention (served by
    ///     <c>ix_domain_events_tags_userid</c>).</item>
    ///   <item><paramref name="from"/>/<paramref name="to"/> — half-open time
    ///     window on <see cref="DomainEvent.CreatedAt"/>: <c>from &lt;= t &lt; to</c>.</item>
    /// </list></para>
    ///
    /// <para><b>Tenant isolation is structural.</b> The read is physically scoped
    /// to the tenant's <c>t_&lt;hex&gt;.domain_events</c> schema via
    /// <c>ITenantDbContextFactory</c>, with a defence-in-depth <c>TenantId</c>
    /// predicate. An empty <paramref name="tenantId"/> throws
    /// <see cref="NotSupportedException"/> — the same hard guard
    /// <see cref="QueryAgentTrailAsync"/> uses — so a cross-tenant read is
    /// unimplementable through this method, not merely unauthorized.</para>
    ///
    /// <para><b>Cursor.</b> Pages on <see cref="DomainEvent.SequenceNumber"/>
    /// (the server-side <c>BIGSERIAL</c> total order), never
    /// <see cref="DomainEvent.CreatedAt"/> (same-millisecond collisions).
    /// <paramref name="cursor"/> is the last <c>SequenceNumber</c> seen; the next
    /// page is <c>SequenceNumber &lt; cursor</c>, most-recent first. Pagination
    /// relies on <c>hasMore</c>/<c>nextCursor</c>, NOT on the total.</para>
    ///
    /// <para><b>Total is opt-in</b> (an unbounded <c>COUNT(*)</c>): computed only
    /// when <paramref name="includeTotal"/> is <c>true</c>; otherwise the returned
    /// <c>Total</c> is <c>null</c> ("not computed", NOT "zero").</para>
    /// </summary>
    Task<(IReadOnlyList<DomainEvent> Events, int? Total)> QueryEventsAsync(
        Guid tenantId,
        string? type, bool typeIsPrefix,
        string? correlationId,
        string? actor,
        DateTimeOffset? from, DateTimeOffset? to,
        long? cursor, int limit, bool includeTotal = false)
        => throw new NotSupportedException(
            "QueryEventsAsync is not implemented by this IEventRepository. " +
            "The time-travel event query (Story 4-7) reads only through the " +
            "tenant-scoped EventRepository, which routes via ITenantDbContextFactory.");
}
