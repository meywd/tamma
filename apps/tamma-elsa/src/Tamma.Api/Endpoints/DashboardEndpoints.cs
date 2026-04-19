using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.Engine;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class DashboardEndpoints
{
    /// <summary>
    /// Audit finding 022 — restored TS shape:
    /// <c>{engineCount, workflowDefinitions, recentEvents[20]}</c>. The
    /// previous <c>{totalEvents, totalWorkflows, timestamp}</c> simplification
    /// dropped the activity feed the dashboard's <c>SummaryTile</c> uses to
    /// render "last event: …".
    /// </summary>
    public static async Task<IResult> GetSummary(
        IEventRepository eventRepo,
        IWorkflowRepository workflowRepo,
        IEngineRegistry engineRegistry,
        TammaDbContext db,
        ITenantContext tc)
    {
        // True total-events count (the previous QueryAsync(..., 1000) was
        // capped at 1000 and presented as if it were a true total).
        var totalEvents = await db.DomainEvents
            .Where(e => tc.TenantId == null || e.TenantId == tc.TenantId)
            .CountAsync();

        var defs = await workflowRepo.ListDefinitionsAsync();
        var (instances, totalWorkflows) = await workflowRepo.ListInstancesAsync(null, tc.TenantId, 1, 1);

        // Activity feed — 20 most recent events scoped to the ambient tenant.
        var recent = await eventRepo.QueryAsync(tc.TenantId, null, null, 20);

        var engines = await engineRegistry.ListAsync(tc.TenantId);

        return Results.Ok(new
        {
            engineCount = engines.Count,
            workflowDefinitions = defs.Count,
            totalWorkflows,
            // Keep `totalEvents` as a useful Tamma-specific counter alongside
            // the TS-required `recentEvents` array.
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
    /// Audit finding 023 — was hard-coded <c>[]</c>; now enumerates the
    /// engine registry filtered to the ambient tenant. Until the real
    /// <c>TammaEngine</c> ports the registry serves a synthetic per-tenant
    /// entry so the dashboard tile is not blank.
    /// </summary>
    public static async Task<IResult> GetEngines(
        IEngineRegistry engineRegistry,
        ITenantContext tc)
    {
        var engines = await engineRegistry.ListAsync(tc.TenantId);
        return Results.Ok(engines);
    }

    /// <summary>
    /// Audit finding 024 — restored TS semantics: one row per workflow
    /// DEFINITION (not instance), each annotated with an
    /// <c>instanceCount</c>. The previous version returned the first 20
    /// instances under the same URL, which was a different semantic and
    /// broke any frontend code written against the TS shape.
    /// </summary>
    public static async Task<IResult> GetWorkflows(
        IWorkflowRepository workflowRepo,
        TammaDbContext db,
        ITenantContext tc)
    {
        var defs = await workflowRepo.ListDefinitionsAsync();

        // Single-query rollup keyed by definition id (avoid N+1).
        var counts = await db.WorkflowInstances
            .Where(i => tc.TenantId == null || i.TenantId == tc.TenantId)
            .GroupBy(i => i.DefinitionId)
            .Select(g => new { DefinitionId = g.Key, Count = g.Count() })
            .ToListAsync();

        var byId = counts.ToDictionary(c => c.DefinitionId, c => c.Count);

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
