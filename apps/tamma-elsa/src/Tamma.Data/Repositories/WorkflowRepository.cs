using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class WorkflowRepository(TammaDbContext db) : IWorkflowRepository
{
    public async Task<WorkflowDefinition> UpsertDefinitionAsync(WorkflowDefinition def)
    {
        var existing = await db.WorkflowDefinitions.FindAsync(def.Id);
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
        def.CreatedAt = DateTime.UtcNow;
        def.UpdatedAt = DateTime.UtcNow;
        def.SyncedAt = DateTime.UtcNow;
        db.WorkflowDefinitions.Add(def);
        await db.SaveChangesAsync();
        return def;
    }

    public async Task<WorkflowDefinition?> GetDefinitionAsync(Guid id)
        => await db.WorkflowDefinitions.FindAsync(id);

    public async Task<List<WorkflowDefinition>> ListDefinitionsAsync()
        => await db.WorkflowDefinitions.OrderByDescending(d => d.UpdatedAt).ToListAsync();

    public async Task<WorkflowInstance> CreateInstanceAsync(WorkflowInstance instance)
    {
        instance.CreatedAt = DateTime.UtcNow;
        instance.UpdatedAt = DateTime.UtcNow;
        db.WorkflowInstances.Add(instance);
        await db.SaveChangesAsync();
        return instance;
    }

    public async Task<WorkflowInstance?> UpdateInstanceAsync(Guid id, Action<WorkflowInstance> update)
    {
        var instance = await db.WorkflowInstances.FindAsync(id);
        if (instance is null) return null;
        update(instance);
        instance.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return instance;
    }

    public async Task<WorkflowInstance?> GetInstanceAsync(Guid id)
        => await db.WorkflowInstances.Include(i => i.Definition).FirstOrDefaultAsync(i => i.Id == id);

    public async Task<bool> DeleteInstanceAsync(Guid id)
    {
        var instance = await db.WorkflowInstances.FindAsync(id);
        if (instance is null) return false;
        db.WorkflowInstances.Remove(instance);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<(List<WorkflowInstance> Instances, int Total)> ListInstancesAsync(
        Guid? definitionId, Guid? tenantId, int page, int pageSize)
    {
        var query = db.WorkflowInstances.AsQueryable();
        if (definitionId.HasValue)
            query = query.Where(i => i.DefinitionId == definitionId.Value);
        if (tenantId.HasValue)
            query = query.Where(i => i.TenantId == tenantId.Value);
        var total = await query.CountAsync();
        var instances = await query
            .OrderByDescending(i => i.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return (instances, total);
    }
}
