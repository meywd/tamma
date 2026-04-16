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
}
