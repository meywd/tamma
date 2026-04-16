using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IProviderHealthRepository
{
    Task RecordSuccessAsync(string providerKey, Guid? tenantId);
    Task RecordFailureAsync(string providerKey, Guid? tenantId);
    Task<ProviderHealth?> GetStatusAsync(string providerKey, Guid? tenantId);
    Task<List<ProviderHealth>> GetAllAsync(Guid? tenantId);
    Task ResetAsync(string providerKey, Guid? tenantId);
}
