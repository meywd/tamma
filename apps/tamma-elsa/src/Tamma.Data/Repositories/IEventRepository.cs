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
