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
/// <para>Cross-tenant admin queries (workflow-instance list with
/// <c>tenantId=null</c>) fall back to <see cref="ControlPlaneDbContext"/>
/// since the factory requires a specific tenant. This is a read-only path
/// used by the dashboard aggregate view — writes always know their
/// tenant.</para>
/// </summary>
public class WorkflowRepository(
    ITenantDbContextFactory tenantDbFactory,
    ITenantContext tenantContext,
    ControlPlaneDbContext cp) : IWorkflowRepository
{
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
        if (tenantContext.TenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            return await db.WorkflowDefinitions.IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.Id == id);
        }
        // System scope — cross-tenant lookup via CP.
        return await cp.WorkflowDefinitions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == id);
    }

    public async Task<List<WorkflowDefinition>> ListDefinitionsAsync()
    {
        if (tenantContext.TenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            return await db.WorkflowDefinitions
                .Where(d => d.TenantId == tid)
                .OrderByDescending(d => d.UpdatedAt).ToListAsync();
        }
        // System scope — all definitions across tenants (admin view).
        return await cp.WorkflowDefinitions.IgnoreQueryFilters()
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
        if (tenantContext.TenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            var instance = await db.WorkflowInstances.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Id == id);
            if (instance is null) return null;
            update(instance);
            instance.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return instance;
        }
        // System scope — locate the instance via CP to learn its tenant,
        // then update through the correct tenant context.
        var found = await cp.WorkflowInstances.IgnoreQueryFilters()
            .Select(i => new { i.Id, i.TenantId })
            .FirstOrDefaultAsync(i => i.Id == id);
        if (found is null) return null;
        if (found.TenantId is null)
        {
            // Platform-scope (null-tenant) instance — update in-place via CP.
            // This is the path the SaaS workflow status/result endpoints hit
            // when the caller is a system integrator without an ambient
            // tenant (self-hosted path, Finding 012 lifecycle bus).
            var cpInstance = await cp.WorkflowInstances.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Id == id);
            if (cpInstance is null) return null;
            update(cpInstance);
            cpInstance.UpdatedAt = DateTime.UtcNow;
            await cp.SaveChangesAsync();
            return cpInstance;
        }
        await using var ctx = await tenantDbFactory.CreateAsync(found.TenantId.Value);
        var ti = await ctx.WorkflowInstances.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == id);
        if (ti is null) return null;
        update(ti);
        ti.UpdatedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
        return ti;
    }

    public async Task<WorkflowInstance?> GetInstanceAsync(Guid id)
    {
        if (tenantContext.TenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            return await db.WorkflowInstances.IgnoreQueryFilters()
                .Include(i => i.Definition)
                .FirstOrDefaultAsync(i => i.Id == id);
        }
        return await cp.WorkflowInstances.IgnoreQueryFilters()
            .Include(i => i.Definition)
            .FirstOrDefaultAsync(i => i.Id == id);
    }

    public async Task<bool> DeleteInstanceAsync(Guid id)
    {
        if (tenantContext.TenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            var instance = await db.WorkflowInstances.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Id == id);
            if (instance is null) return false;
            db.WorkflowInstances.Remove(instance);
            await db.SaveChangesAsync();
            return true;
        }
        var found = await cp.WorkflowInstances.IgnoreQueryFilters()
            .Select(i => new { i.Id, i.TenantId })
            .FirstOrDefaultAsync(i => i.Id == id);
        if (found is null || found.TenantId is null) return false;
        await using var ctx = await tenantDbFactory.CreateAsync(found.TenantId.Value);
        var ti = await ctx.WorkflowInstances.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == id);
        if (ti is null) return false;
        ctx.WorkflowInstances.Remove(ti);
        await ctx.SaveChangesAsync();
        return true;
    }

    public async Task<(List<WorkflowInstance> Instances, int Total)> ListInstancesAsync(
        Guid? definitionId, Guid? tenantId, int page, int pageSize)
    {
        if (tenantId is Guid tid)
        {
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
        // Cross-tenant admin view via CP.
        var q = cp.WorkflowInstances.IgnoreQueryFilters().AsQueryable();
        if (definitionId.HasValue)
            q = q.Where(i => i.DefinitionId == definitionId.Value);
        var t = await q.CountAsync();
        var list = await q
            .OrderByDescending(i => i.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return (list, t);
    }
}
