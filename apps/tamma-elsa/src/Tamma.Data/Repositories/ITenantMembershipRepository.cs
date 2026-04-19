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

    /// <summary>
    /// Returns the number of owner-role memberships in the tenant. Drives
    /// the last-owner guard in findings 012 (demote) and 013 (remove).
    /// </summary>
    Task<int> CountOwnersAsync(Guid tenantId);

    /// <summary>Returns every membership row for the tenant (no paging).</summary>
    Task<List<TenantMembership>> ListAllByTenantAsync(Guid tenantId);
}
