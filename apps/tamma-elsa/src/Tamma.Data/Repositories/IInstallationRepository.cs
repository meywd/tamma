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

    // ── Router-service additions ────────────────────────────────────────────
    /// <summary>Create a new installation row. Throws on conflicting InstallationId.</summary>
    Task<GitHubInstallation> CreateAsync(GitHubInstallation install);

    /// <summary>Soft-delete: mark SuspendedAt so the row stays around for audit.</summary>
    Task SoftDeleteAsync(long installationId);

    /// <summary>Flip SuspendedAt on/off based on the <paramref name="suspended"/> flag.</summary>
    Task SetSuspendedAsync(long installationId, bool suspended);

    /// <summary>Insert (or reactivate) a repo row for the given installation entity.</summary>
    Task AddRepoAsync(Guid installationEntityId, long repoId, string repoFullName);

    /// <summary>Soft-delete a repo row by flipping IsActive=false.</summary>
    Task RemoveRepoAsync(Guid installationEntityId, long repoId);
}
