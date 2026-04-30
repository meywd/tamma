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
}
