using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class InstallationRepository(TammaDbContext db) : IInstallationRepository
{
    public async Task<GitHubInstallation> UpsertAsync(GitHubInstallation installation)
    {
        var existing = await db.GitHubInstallations
            .FirstOrDefaultAsync(i => i.InstallationId == installation.InstallationId);
        if (existing is not null)
        {
            existing.AccountLogin = installation.AccountLogin;
            existing.AccountType = installation.AccountType;
            existing.AppId = installation.AppId;
            existing.AppSlug = installation.AppSlug;
            existing.Permissions = installation.Permissions;
            existing.TenantId = installation.TenantId;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.SuspendedAt = null;
            await db.SaveChangesAsync();
            return existing;
        }
        installation.CreatedAt = DateTime.UtcNow;
        installation.UpdatedAt = DateTime.UtcNow;
        db.GitHubInstallations.Add(installation);
        await db.SaveChangesAsync();
        return installation;
    }

    public async Task<GitHubInstallation?> GetByInstallationIdAsync(long installationId)
        => await db.GitHubInstallations
            .Include(i => i.Repos)
            .FirstOrDefaultAsync(i => i.InstallationId == installationId);

    public async Task<GitHubInstallation?> GetByEntityIdAsync(Guid entityId)
        => await db.GitHubInstallations
            .Include(i => i.Repos)
            .FirstOrDefaultAsync(i => i.Id == entityId);

    public async Task<List<GitHubInstallation>> ListAsync()
        => await db.GitHubInstallations.Include(i => i.Repos).ToListAsync();

    public async Task<List<GitHubInstallation>> ListActiveAsync()
        => await db.GitHubInstallations
            .Where(i => i.SuspendedAt == null)
            .Include(i => i.Repos.Where(r => r.IsActive))
            .ToListAsync();

    public async Task DeleteAsync(long installationId)
    {
        var installation = await db.GitHubInstallations
            .FirstOrDefaultAsync(i => i.InstallationId == installationId);
        if (installation is not null)
        {
            db.GitHubInstallations.Remove(installation);
            await db.SaveChangesAsync();
        }
    }

    public async Task SetReposAsync(Guid installationEntityId, List<GitHubInstallationRepo> repos)
    {
        var existing = await db.GitHubInstallationRepos
            .Where(r => r.InstallationEntityId == installationEntityId).ToListAsync();
        db.GitHubInstallationRepos.RemoveRange(existing);
        foreach (var repo in repos)
            repo.InstallationEntityId = installationEntityId;
        db.GitHubInstallationRepos.AddRange(repos);
        await db.SaveChangesAsync();
    }

    public async Task<List<GitHubInstallationRepo>> ListReposAsync(Guid installationEntityId)
        => await db.GitHubInstallationRepos
            .Where(r => r.InstallationEntityId == installationEntityId)
            .ToListAsync();

    public async Task SuspendAsync(long installationId)
    {
        var installation = await db.GitHubInstallations
            .FirstOrDefaultAsync(i => i.InstallationId == installationId);
        if (installation is not null)
        {
            installation.SuspendedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task UnsuspendAsync(long installationId)
    {
        var installation = await db.GitHubInstallations
            .FirstOrDefaultAsync(i => i.InstallationId == installationId);
        if (installation is not null)
        {
            installation.SuspendedAt = null;
            await db.SaveChangesAsync();
        }
    }

    // ── Router-service additions ────────────────────────────────────────────

    public async Task<GitHubInstallation> CreateAsync(GitHubInstallation install)
    {
        install.CreatedAt = DateTime.UtcNow;
        install.UpdatedAt = DateTime.UtcNow;
        db.GitHubInstallations.Add(install);
        await db.SaveChangesAsync();
        return install;
    }

    public async Task SoftDeleteAsync(long installationId)
    {
        var installation = await db.GitHubInstallations
            .FirstOrDefaultAsync(i => i.InstallationId == installationId);
        if (installation is not null)
        {
            // Use SuspendedAt as the soft-delete marker — keeps the row for audit.
            installation.SuspendedAt = DateTime.UtcNow;
            installation.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task SetSuspendedAsync(long installationId, bool suspended)
    {
        var installation = await db.GitHubInstallations
            .FirstOrDefaultAsync(i => i.InstallationId == installationId);
        if (installation is not null)
        {
            installation.SuspendedAt = suspended ? DateTime.UtcNow : null;
            installation.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task AddRepoAsync(Guid installationEntityId, long repoId, string repoFullName)
    {
        var existing = await db.GitHubInstallationRepos
            .FirstOrDefaultAsync(r =>
                r.InstallationEntityId == installationEntityId && r.RepoId == repoId);

        if (existing is not null)
        {
            // Reactivate + refresh name if it changed.
            existing.IsActive = true;
            existing.RepoFullName = repoFullName;
        }
        else
        {
            db.GitHubInstallationRepos.Add(new GitHubInstallationRepo
            {
                InstallationEntityId = installationEntityId,
                RepoId = repoId,
                RepoFullName = repoFullName,
                IsActive = true
            });
        }

        await db.SaveChangesAsync();
    }

    public async Task RemoveRepoAsync(Guid installationEntityId, long repoId)
    {
        var repo = await db.GitHubInstallationRepos
            .FirstOrDefaultAsync(r =>
                r.InstallationEntityId == installationEntityId && r.RepoId == repoId);
        if (repo is not null)
        {
            repo.IsActive = false;
            await db.SaveChangesAsync();
        }
    }
}
