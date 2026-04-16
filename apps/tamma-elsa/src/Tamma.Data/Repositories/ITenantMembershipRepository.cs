using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface ITenantMembershipRepository
{
    Task<TenantMembership> AddAsync(Guid tenantId, Guid userId, string role);
    Task RemoveAsync(Guid tenantId, Guid userId);
    Task<string?> GetRoleAsync(Guid tenantId, Guid userId);
    Task<(List<TenantMembership> Members, int Total)> ListByTenantAsync(Guid tenantId, int limit, int offset);
    Task<List<TenantMembership>> GetUserTenantsAsync(Guid userId);
    Task UpdateRoleAsync(Guid tenantId, Guid userId, string role);
}
