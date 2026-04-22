using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.Engine;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class DashboardEndpoints
{
    /// <summary>
    /// Audit finding 022 — restored TS shape:
    /// <c>{engineCount, workflowDefinitions, recentEvents[20]}</c>.
    /// </summary>
    public static async Task<IResult> GetSummary(
        IEventRepository eventRepo,
        IWorkflowRepository workflowRepo,
        IEngineRegistry engineRegistry,
        ITenantDbContextFactory tenantDbFactory,
        ITenantContext tc)
    {
        long totalEvents = 0;
        var defs = await workflowRepo.ListDefinitionsAsync();
        var (instances, totalWorkflows) = await workflowRepo.ListInstancesAsync(null, tc.TenantId, 1, 1);

        // Activity feed — 20 most recent events scoped to the ambient tenant.
        var recent = await eventRepo.QueryAsync(tc.TenantId, null, null, 20);

        // Total events count — scoped to the current tenant via the factory.
        if (tc.TenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            totalEvents = await db.DomainEvents.LongCountAsync(e => e.TenantId == tid);
        }

        var engines = await engineRegistry.ListAsync(tc.TenantId);

        return Results.Ok(new
        {
            engineCount = engines.Count,
            workflowDefinitions = defs.Count,
            totalWorkflows,
            totalEvents,
            recentEvents = recent.Select(e => new
            {
                id = e.Id,
                type = e.Type,
                createdAt = e.CreatedAt,
                issueNumber = e.IssueNumber,
                tenantId = e.TenantId
            }),
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Audit finding 023 — enumerates the engine registry filtered to the
    /// ambient tenant.
    /// </summary>
    public static async Task<IResult> GetEngines(
        IEngineRegistry engineRegistry,
        ITenantContext tc)
    {
        var engines = await engineRegistry.ListAsync(tc.TenantId);
        return Results.Ok(engines);
    }

    /// <summary>
    /// Audit finding 024 — one row per workflow DEFINITION annotated with
    /// <c>instanceCount</c>, keyed off the tenant-scoped instance store.
    /// </summary>
    public static async Task<IResult> GetWorkflows(
        IWorkflowRepository workflowRepo,
        ITenantDbContextFactory tenantDbFactory,
        ITenantContext tc)
    {
        var defs = await workflowRepo.ListDefinitionsAsync();

        // Single-query rollup keyed by definition id (avoid N+1). Scoped to
        // the ambient tenant via the factory — no cross-tenant leak possible.
        var byId = new Dictionary<Guid, int>();
        if (tc.TenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            var counts = await db.WorkflowInstances
                .Where(i => i.TenantId == tid)
                .GroupBy(i => i.DefinitionId)
                .Select(g => new { DefinitionId = g.Key, Count = g.Count() })
                .ToListAsync();
            byId = counts.ToDictionary(c => c.DefinitionId, c => c.Count);
        }

        var rollup = defs.Select(d => new
        {
            id = d.Id,
            name = d.Name,
            description = d.Description,
            version = d.Version,
            syncedAt = d.SyncedAt,
            instanceCount = byId.TryGetValue(d.Id, out var c) ? c : 0
        });

        return Results.Ok(rollup);
    }
}
