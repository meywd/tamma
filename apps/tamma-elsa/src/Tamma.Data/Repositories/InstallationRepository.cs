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
        var now = DateTime.UtcNow;
        foreach (var repo in repos)
        {
            repo.InstallationEntityId = installationEntityId;
            EnsureOwnerNameFromFullName(repo);
            if (repo.CreatedAt == default) repo.CreatedAt = now;
            repo.UpdatedAt = now;
        }
        db.GitHubInstallationRepos.AddRange(repos);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Backfill Owner / Name from RepoFullName so callers that previously only
    /// supplied <c>RepoFullName</c> still produce well-formed rows after the
    /// hardening migration (finding 018) requires both columns to be NOT NULL.
    /// </summary>
    private static void EnsureOwnerNameFromFullName(GitHubInstallationRepo repo)
    {
        if (string.IsNullOrEmpty(repo.RepoFullName)) return;
        if (!string.IsNullOrEmpty(repo.Owner) && !string.IsNullOrEmpty(repo.Name)) return;
        var slash = repo.RepoFullName.IndexOf('/');
        if (slash > 0 && slash < repo.RepoFullName.Length - 1)
        {
            repo.Owner = repo.RepoFullName[..slash];
            repo.Name = repo.RepoFullName[(slash + 1)..];
        }
        else
        {
            // Fallback: avoid NOT NULL violation when full_name has no slash.
            repo.Owner = repo.RepoFullName;
            repo.Name = repo.RepoFullName;
        }
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

        var now = DateTime.UtcNow;
        if (existing is not null)
        {
            // Reactivate + refresh name if it changed.
            existing.IsActive = true;
            existing.RepoFullName = repoFullName;
            existing.UpdatedAt = now;
            EnsureOwnerNameFromFullName(existing);
        }
        else
        {
            var repo = new GitHubInstallationRepo
            {
                InstallationEntityId = installationEntityId,
                RepoId = repoId,
                RepoFullName = repoFullName,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            EnsureOwnerNameFromFullName(repo);
            db.GitHubInstallationRepos.Add(repo);
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

    public async Task<GitHubInstallation?> GetByRepoFullNameAsync(string repoFullName)
    {
        // Join via the active-only repo view so that a repo removed from the
        // installation no longer resolves back to the installation row.
        var repo = await db.GitHubInstallationRepos
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.IsActive && r.RepoFullName == repoFullName);
        if (repo is null) return null;
        return await db.GitHubInstallations
            .FirstOrDefaultAsync(i => i.Id == repo.InstallationEntityId);
    }

    /// <summary>
    /// Story 18-4 — eager-load the per-installation repo collection so the
    /// onboarding wizard can render counts + listings without a N+1 round
    /// trip. Suspended rows are included; the dashboard surfaces them with a
    /// "re-enable on GitHub" banner. Newest-first because freshly-installed
    /// orgs are the most common thing the user wants to confirm.
    /// </summary>
    public async Task<List<GitHubInstallation>> ListByTenantAsync(Guid tenantId)
        => await db.GitHubInstallations
            .Where(i => i.TenantId == tenantId)
            .Include(i => i.Repos)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();
}
