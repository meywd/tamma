using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IInstallationRepository
{
    Task<GitHubInstallation> UpsertAsync(GitHubInstallation installation);
    Task<GitHubInstallation?> GetByInstallationIdAsync(long installationId);
    Task<List<GitHubInstallation>> ListAsync();
    Task<List<GitHubInstallation>> ListActiveAsync();
    Task DeleteAsync(long installationId);
    Task SetReposAsync(Guid installationEntityId, List<GitHubInstallationRepo> repos);
    Task<List<GitHubInstallationRepo>> ListReposAsync(Guid installationEntityId);
    Task SuspendAsync(long installationId);
    Task UnsuspendAsync(long installationId);
}
