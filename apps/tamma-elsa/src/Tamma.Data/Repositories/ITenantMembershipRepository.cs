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

    /// <summary>
    /// Returns the ids + names of every tenant where the given user is the
    /// sole owner-role member. Drives the admin <c>DeleteUser</c> sole-owner
    /// guard (audit finding auth/019). An empty list means the user can be
    /// deleted safely; a non-empty list means the caller must transfer or
    /// promote another member first.
    /// </summary>
    Task<List<SoleOwnedTenant>> ListSoleOwnedTenantsAsync(Guid userId);

    /// <summary>
    /// Soft-delete every membership row for the user in a single DB
    /// round-trip. Used by the admin <c>DeleteUser</c> cascade — the row
    /// count is irrelevant so no return value.
    /// </summary>
    Task RemoveAllForUserAsync(Guid userId);
}

/// <summary>
/// Projection used by <see cref="ITenantMembershipRepository.ListSoleOwnedTenantsAsync"/>.
/// </summary>
public sealed record SoleOwnedTenant(Guid TenantId, string Name, string Slug);
