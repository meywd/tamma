using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class DashboardEndpoints
{
    public static async Task<IResult> GetSummary(
        IEventRepository eventRepo,
        IWorkflowRepository workflowRepo,
        ITenantContext tc)
    {
        var events = await eventRepo.QueryAsync(tc.TenantId, null, null, 1000);
        var (instances, total) = await workflowRepo.ListInstancesAsync(null, tc.TenantId, 1, 1);
        return Results.Ok(new
        {
            totalEvents = events.Count,
            totalWorkflows = total,
            timestamp = DateTime.UtcNow
        });
    }

    public static Task<IResult> GetEngines() =>
        Task.FromResult(Results.Ok(Array.Empty<object>()));

    public static async Task<IResult> GetWorkflows(IWorkflowRepository workflowRepo, ITenantContext tc)
    {
        var (instances, total) = await workflowRepo.ListInstancesAsync(null, tc.TenantId, 1, 20);
        return Results.Ok(new { instances = instances.Select(i => new { i.Id, i.DefinitionId, i.Status, i.CreatedAt }), total });
    }
}
