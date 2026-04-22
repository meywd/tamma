using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IInstallationRepository
{
    Task<GitHubInstallation> UpsertAsync(GitHubInstallation installation);
    Task<GitHubInstallation?> GetByInstallationIdAsync(long installationId);

    /// <summary>
    /// Lookup by the <c>github_installations.Id</c> (C# entity primary key),
    /// not the GitHub-issued <c>InstallationId</c>. Callers that only have
    /// the entity-id (e.g. from <c>api_keys.OwnerId</c>) use this overload.
    /// </summary>
    Task<GitHubInstallation?> GetByEntityIdAsync(Guid entityId);
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

    /// <summary>
    /// Reverse-lookup: given a repo's <c>owner/name</c> full name, find the
    /// installation that grants access to it. Used by the engine callback
    /// service (audit engine findings 005-011) to map incoming
    /// <c>?repo=owner/name</c> query params back to an installation id so it
    /// can mint an access token. Returns null when no active repo row
    /// matches; the caller treats that as 503 <c>github_client_not_configured</c>.
    /// </summary>
    Task<GitHubInstallation?> GetByRepoFullNameAsync(string repoFullName);
}
