using Tamma.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Tenant-scoped workflow repo. Uses <see cref="ITenantDbContextFactory"/>
/// for all reads/writes; the tenant id is carried on the entity itself
/// (<c>WorkflowDefinition.TenantId</c>, <c>WorkflowInstance.TenantId</c>)
/// or resolved from <see cref="ITenantContext"/> for queries where the
/// ambient tenant is implicit.
///
/// <para>Story 28-1 PR D: workflow_definitions / workflow_instances moved
/// off <see cref="ControlPlaneDbContext"/>. Cross-tenant admin queries
/// (no ambient tenant, no explicit <c>tenantId</c>) are not implemented —
/// per Decision #2 there is no current user story for "admin views every
/// tenant's workflow", so those paths throw <see cref="NotSupportedException"/>
/// rather than silently fan out across every active tenant. Build the
/// fan-out when a story demands it.</para>
/// </summary>
public class WorkflowRepository(
    ITenantDbContextFactory tenantDbFactory,
    ITenantContext tenantContext) : IWorkflowRepository
{
    private static InvalidOperationException MissingTenantException(string operation)
        => new(
            $"WorkflowRepository.{operation} requires an ambient tenant id. " +
            "Story 28-1 PR D moved workflow_definitions / workflow_instances " +
            "off the control plane; cross-tenant admin queries are not " +
            "implemented. See Decision #2 in " +
            ".dev/decisions/story-28-1-design-calls.md.");

    public async Task<WorkflowDefinition> UpsertDefinitionAsync(WorkflowDefinition def)
    {
        var tid = def.TenantId ?? tenantContext.TenantId
            ?? throw new InvalidOperationException(
                "Cannot upsert a workflow definition without a tenant id. "
                + "Set WorkflowDefinition.TenantId or bind ITenantContext before calling.");

        await using var db = await tenantDbFactory.CreateAsync(tid);

        WorkflowDefinition? existing = null;
        if (def.Id != Guid.Empty)
        {
            existing = await db.WorkflowDefinitions.IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.Id == def.Id && d.TenantId == tid);
        }
        if (existing is null)
        {
            existing = await db.WorkflowDefinitions.IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.Name == def.Name && d.TenantId == tid);
        }

        if (existing is not null)
        {
            existing.Name = def.Name;
            existing.Description = def.Description;
            existing.Steps = def.Steps;
            existing.Version++;
            existing.SyncedAt = DateTime.UtcNow;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return existing;
        }
        def.TenantId = tid;
        def.CreatedAt = DateTime.UtcNow;
        def.UpdatedAt = DateTime.UtcNow;
        def.SyncedAt = DateTime.UtcNow;
        db.WorkflowDefinitions.Add(def);
        await db.SaveChangesAsync();
        return def;
    }

    public async Task<WorkflowDefinition?> GetDefinitionAsync(Guid id)
    {
        var tid = tenantContext.TenantId
            ?? throw MissingTenantException(nameof(GetDefinitionAsync));
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await db.WorkflowDefinitions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == id);
    }

    public async Task<List<WorkflowDefinition>> ListDefinitionsAsync()
    {
        var tid = tenantContext.TenantId
            ?? throw MissingTenantException(nameof(ListDefinitionsAsync));
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await db.WorkflowDefinitions
            .Where(d => d.TenantId == tid)
            .OrderByDescending(d => d.UpdatedAt).ToListAsync();
    }

    public async Task<WorkflowInstance> CreateInstanceAsync(WorkflowInstance instance)
    {
        var tid = instance.TenantId ?? tenantContext.TenantId
            ?? throw new InvalidOperationException(
                "Cannot create a workflow instance without a tenant id.");

        instance.TenantId = tid;
        instance.CreatedAt = DateTime.UtcNow;
        instance.UpdatedAt = DateTime.UtcNow;

        await using var db = await tenantDbFactory.CreateAsync(tid);
        db.WorkflowInstances.Add(instance);
        await db.SaveChangesAsync();
        return instance;
    }

    public async Task<WorkflowInstance?> UpdateInstanceAsync(Guid id, Action<WorkflowInstance> update)
    {
        var tid = tenantContext.TenantId
            ?? throw MissingTenantException(nameof(UpdateInstanceAsync));
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var instance = await db.WorkflowInstances.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == id);
        if (instance is null) return null;
        update(instance);
        instance.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return instance;
    }

    public async Task<WorkflowInstance?> GetInstanceAsync(Guid id)
    {
        var tid = tenantContext.TenantId
            ?? throw MissingTenantException(nameof(GetInstanceAsync));
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await db.WorkflowInstances.IgnoreQueryFilters()
            .Include(i => i.Definition)
            .FirstOrDefaultAsync(i => i.Id == id);
    }

    public async Task<bool> DeleteInstanceAsync(Guid id)
    {
        var tid = tenantContext.TenantId
            ?? throw MissingTenantException(nameof(DeleteInstanceAsync));
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var instance = await db.WorkflowInstances.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == id);
        if (instance is null) return false;
        db.WorkflowInstances.Remove(instance);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<(List<WorkflowInstance> Instances, int Total)> ListInstancesAsync(
        Guid? definitionId, Guid? tenantId, int page, int pageSize)
    {
        var tid = tenantId ?? tenantContext.TenantId
            ?? throw MissingTenantException(nameof(ListInstancesAsync));
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var query = db.WorkflowInstances.Where(i => i.TenantId == tid);
        if (definitionId.HasValue)
            query = query.Where(i => i.DefinitionId == definitionId.Value);
        var total = await query.CountAsync();
        var instances = await query
            .OrderByDescending(i => i.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return (instances, total);
    }

    public async Task<WorkflowInstanceSummary> SummarizeInstancesAsync(
        Guid tenantId, DateTime? from, DateTime? to)
    {
        // Structurally tenant-scoped: the tenant db + the TenantId equality both
        // fence the aggregate to this tenant's rows. Callers pass a concrete
        // tenant id (the endpoint fails closed on a null/empty ambient tenant
        // BEFORE reaching here), so there is no cross-tenant fan-out.
        await using var db = await tenantDbFactory.CreateAsync(tenantId);
        var query = db.WorkflowInstances.Where(i => i.TenantId == tenantId);
        if (from.HasValue)
            query = query.Where(i => i.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(i => i.CreatedAt < to.Value);

        // Per-status counts: a single GROUP BY status COUNT(*) in SQL. The record
        // is materialised in memory (anonymous SQL projection → record) so EF only
        // has to translate the group/count, never a record constructor.
        var byStatusRaw = await query
            .GroupBy(i => i.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        // Per-definition counts: GROUP BY definition_id COUNT(*). Names are joined
        // in memory from the tenant's definition list (a small set) so the SQL stays
        // a plain grouped count and there is no string[]/array-parameter predicate.
        var byDefinitionRaw = await query
            .GroupBy(i => i.DefinitionId)
            .Select(g => new { DefinitionId = g.Key, Count = g.Count() })
            .ToListAsync();

        var definitionNames = await db.WorkflowDefinitions
            .Where(d => d.TenantId == tenantId)
            .Select(d => new { d.Id, d.Name })
            .ToListAsync();
        var nameById = definitionNames.ToDictionary(d => d.Id, d => d.Name);

        var byStatus = byStatusRaw
            .Select(s => new WorkflowStatusCount(s.Status, s.Count))
            .OrderByDescending(s => s.Count)
            .ToList();

        var byDefinition = byDefinitionRaw
            .Select(d => new WorkflowDefinitionCount(
                d.DefinitionId,
                nameById.TryGetValue(d.DefinitionId, out var name) && !string.IsNullOrWhiteSpace(name)
                    ? name
                    : d.DefinitionId.ToString(),
                d.Count))
            .OrderByDescending(d => d.Count)
            .ToList();

        var total = byStatusRaw.Sum(s => s.Count);
        return new WorkflowInstanceSummary(total, byStatus, byDefinition);
    }
}
