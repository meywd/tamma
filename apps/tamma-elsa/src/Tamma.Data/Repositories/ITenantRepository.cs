using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface ITenantRepository
{
    Task<Tenant> CreateAsync(Tenant tenant);
    Task<Tenant?> GetByIdAsync(Guid id);
    Task<Tenant?> GetBySlugAsync(string slug);
    Task<Tenant?> GetByExternalIdAsync(string externalId);
    Task<Tenant> UpdateAsync(Tenant tenant);
    Task SoftDeleteAsync(Guid id);
    Task<List<Tenant>> ListByUserAsync(Guid userId);

    /// <summary>
    /// Returns membership-joined tenant rows for the user, including the
    /// caller's role in each and the join timestamp. Drives finding 019's
    /// expanded <c>GET /api/v1/tenants</c> response.
    /// </summary>
    Task<List<TenantMembershipView>> ListMembershipsByUserAsync(Guid userId);
}

/// <summary>
/// Projection used by <see cref="ITenantRepository.ListMembershipsByUserAsync"/>
/// — flattens a tenant row with the caller's membership role + join date.
/// </summary>
public sealed record TenantMembershipView(
    Tenant Tenant,
    string Role,
    DateTime JoinedAt);
