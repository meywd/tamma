using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IInviteRepository
{
    Task<UserInvite> CreateAsync(UserInvite invite);
    Task<UserInvite?> GetByTokenHashAsync(string tokenHash);
    Task AcceptAsync(Guid id);
    Task<List<UserInvite>> ListPendingByTenantAsync(Guid tenantId);
    Task DeleteAsync(Guid id);
}
