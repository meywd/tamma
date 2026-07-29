using Microsoft.EntityFrameworkCore;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Tenant-scoped <c>iterations</c> repository (Story 44-1; populated by 44-4).
/// Same seam shape as <see cref="ProjectRepository"/>.
/// </summary>
public class IterationRepository(
    ITenantDbContextFactory tenantDbFactory,
    ITenantContext tenantContext) : IIterationRepository
{
    private static readonly string[] s_statuses = ["planned", "active", "closed"];

    private Guid RequireTenantId() => tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "IterationRepository requires an ambient tenant id. Tracker tables are "
            + "tenant-schema resident (Epic 44 D5).");

    public async Task<IterationEntity?> GetAsync(Guid id)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await db.Iterations.FirstOrDefaultAsync(i => i.Id == id);
    }

    public async Task<List<IterationEntity>> ListByProjectAsync(Guid projectId)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await db.Iterations
            .Where(i => i.ProjectId == projectId)
            .OrderBy(i => i.StartsOn)
            .ThenBy(i => i.Name)
            .ToListAsync();
    }

    public async Task<IterationEntity> CreateAsync(IterationEntity iteration)
    {
        ArgumentNullException.ThrowIfNull(iteration);
        ValidateStatus(iteration.Status);

        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);

        if (iteration.Id == Guid.Empty)
            iteration.Id = UuidV7.NewGuid();
        iteration.CreatedAt = DateTime.UtcNow;
        iteration.UpdatedAt = iteration.CreatedAt;
        iteration.Version = 1;

        db.Iterations.Add(iteration);
        await db.SaveChangesAsync();
        return iteration;
    }

    public async Task<IterationEntity?> UpdateAsync(IterationEntity iteration)
    {
        ArgumentNullException.ThrowIfNull(iteration);
        ValidateStatus(iteration.Status);

        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var existing = await db.Iterations.FirstOrDefaultAsync(i => i.Id == iteration.Id);
        if (existing is null)
            return null;

        existing.Name = iteration.Name;
        existing.StartsOn = iteration.StartsOn;
        existing.EndsOn = iteration.EndsOn;
        existing.Status = iteration.Status;
        existing.CapacityPoints = iteration.CapacityPoints;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.Version += 1;
        await db.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var row = await db.Iterations.FirstOrDefaultAsync(i => i.Id == id);
        if (row is null)
            return false;
        db.Iterations.Remove(row);
        // work_items.IterationId is FK SET NULL — items outlive sprints.
        await db.SaveChangesAsync();
        return true;
    }

    private static void ValidateStatus(string status)
    {
        if (!s_statuses.Contains(status, StringComparer.Ordinal))
        {
            throw new TammaError(
                "TRACKER.UNKNOWN_ITERATION_STATUS",
                $"Unknown iteration status: '{status}'. Valid statuses: planned, active, closed.",
                new Dictionary<string, object?> { ["input"] = status },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }
    }
}
