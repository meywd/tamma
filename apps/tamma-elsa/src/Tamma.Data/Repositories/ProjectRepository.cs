using Microsoft.EntityFrameworkCore;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Tracking;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Tenant-scoped <c>projects</c> repository (Story 44-1). Mirrors
/// <see cref="AcceptanceRulesRepository"/>'s seam shape: all reads/writes go
/// through <see cref="ITenantDbContextFactory"/> with the ambient
/// <see cref="ITenantContext"/> tenant id (both operating modes land in the
/// tenant's own schema — single-user users own a personal tenant).
/// </summary>
public class ProjectRepository(
    ITenantDbContextFactory tenantDbFactory,
    ITenantContext tenantContext) : IProjectRepository
{
    private Guid RequireTenantId() => tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "ProjectRepository requires an ambient tenant id. Tracker tables are "
            + "tenant-schema resident (Epic 44 D5); there is no control-plane home for them.");

    public async Task<ProjectEntity?> GetAsync(Guid id)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await db.Projects.FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<ProjectEntity?> GetByKeyAsync(string key)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await db.Projects.FirstOrDefaultAsync(p => p.Key == key);
    }

    public async Task<List<ProjectEntity>> ListAsync(bool includeArchived = false)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var query = db.Projects.AsQueryable();
        if (!includeArchived)
            query = query.Where(p => p.ArchivedAt == null);
        return await query.OrderBy(p => p.Key).ToListAsync();
    }

    public async Task<ProjectEntity> CreateAsync(ProjectEntity project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ValidateVocabulary(project);

        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);

        if (project.Id == Guid.Empty)
            project.Id = UuidV7.NewGuid();
        project.NextNumber = project.NextNumber < 1 ? 1 : project.NextNumber;
        project.CreatedAt = DateTime.UtcNow;
        project.UpdatedAt = project.CreatedAt;
        project.Version = 1;

        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project;
    }

    public async Task<ProjectEntity?> UpdateAsync(ProjectEntity project, int? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ValidateVocabulary(project);

        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var existing = await db.Projects.FirstOrDefaultAsync(p => p.Id == project.Id);
        if (existing is null)
            return null;

        // Key and NextNumber deliberately NOT copied: the key prefix is frozen
        // (a re-key flows through IWorkItemRepository.RekeyAsync per item) and
        // the counter belongs to the mint.
        existing.Name = project.Name;
        existing.Description = project.Description;
        existing.RepositoryId = project.RepositoryId;
        existing.EstimateScale = project.EstimateScale;
        existing.ArchivedAt = project.ArchivedAt;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.Version += 1;
        PinExpectedVersion(db, existing, expectedVersion);
        await SaveGuardingVersionAsync(db, project.Id);
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id, int? expectedVersion = null)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var row = await db.Projects.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null)
            return false;
        db.Projects.Remove(row);
        PinExpectedVersion(db, row, expectedVersion);
        // A non-empty project trips the work_items FK RESTRICT here — the
        // caller (44-2) maps SqlState 23503 to the documented 409.
        await SaveGuardingVersionAsync(db, id);
        return true;
    }

    /// <summary>
    /// Make the caller's <c>If-Match</c> precondition ATOMIC with the write
    /// (44-2 adversarial review, 2026-07-29). Without this the service checks
    /// the version against ITS read and the repository then re-reads in a fresh
    /// context, so the interleaving
    /// <c>W2.read(v1) → W1 completes(v2) → W2.repo-read(v2) → W2 writes v3</c>
    /// passes the service check and never trips the EF token. Pinning the
    /// concurrency token's ORIGINAL value to the version the caller asserted
    /// puts <c>WHERE "Version" = @expected</c> in the UPDATE/DELETE itself:
    /// rowcount 0 → DbUpdateConcurrencyException → the typed conflict.
    /// </summary>
    private static void PinExpectedVersion(TenantDbContext db, ProjectEntity row, int? expectedVersion)
    {
        if (expectedVersion is int expected)
            db.Entry(row).Property(p => p.Version).OriginalValue = expected;
    }

    private static async Task SaveGuardingVersionAsync(TenantDbContext db, Guid projectId)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new TammaError(
                "TRACKER.CONCURRENCY_CONFLICT",
                $"Project '{projectId}' was modified by another writer while this update "
                + "was in flight (optimistic-concurrency Version mismatch). Re-read the project "
                + "and retry the operation against its current state.",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["conflictEntries"] = ex.Entries.Count,
                },
                retryable: true,
                severity: TammaErrorSeverity.Medium);
        }
    }

    private static void ValidateVocabulary(ProjectEntity project)
    {
        if (!WorkItemRef.IsValidProjectKey(project.Key))
        {
            throw new TammaError(
                "TRACKER.INVALID_WORK_ITEM_KEY",
                $"Invalid project key: '{project.Key}'. A project key is 2-10 characters, "
                + "upper-case A-Z0-9, starting with a letter (^[A-Z][A-Z0-9]{1,9}$). "
                + "Keys are never normalized — fix the input.",
                new Dictionary<string, object?> { ["projectKey"] = project.Key },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        // Parse (not TryParse) — an unknown scale is a fail-loud TammaError
        // with code TRACKER.UNKNOWN_ESTIMATE_SCALE, never a DB CHECK surprise.
        _ = EstimateScaleExtensions.Parse(project.EstimateScale);
    }
}
