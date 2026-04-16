using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IUserRepository
{
    Task<User> CreateAsync(User user);
    Task<User?> GetByIdAsync(Guid id);
    Task<User?> GetByEmailAsync(string email);
    Task<User?> GetByGitHubIdAsync(int githubId);
    Task<(List<User> Users, int Total)> ListAsync(int limit, int offset, string? role);
    Task<User> UpdateAsync(User user);
    Task SoftDeleteAsync(Guid id);
    Task UpdateActiveTenantAsync(Guid userId, Guid tenantId);
}
