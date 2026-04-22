using System.Text.Json;
using Tamma.Api.Dtos.Workflows;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class WorkflowEndpoints
{
    public static async Task<IResult> CreateDefinition(
        CreateDefinitionRequest req,
        IWorkflowRepository workflowRepo,
        ITenantContext tc)
    {
        var def = await workflowRepo.UpsertDefinitionAsync(new WorkflowDefinition
        {
            Name = req.Name,
            Description = req.Description,
            Steps = req.Steps is not null ? JsonSerializer.Serialize(req.Steps) : "[]",
            TenantId = tc.TenantId
        });
        return Results.Created($"/api/workflows/definitions/{def.Id}",
            new DefinitionResponse(def.Id, def.Name, def.Version, def.Description, def.SyncedAt));
    }

    public static async Task<IResult> ListDefinitions(IWorkflowRepository workflowRepo)
    {
        var defs = await workflowRepo.ListDefinitionsAsync();
        return Results.Ok(defs.Select(d =>
            new DefinitionResponse(d.Id, d.Name, d.Version, d.Description, d.SyncedAt)));
    }

    public static async Task<IResult> CreateInstance(
        CreateInstanceRequest req,
        IWorkflowRepository workflowRepo,
        ITenantContext tc)
    {
        var instance = await workflowRepo.CreateInstanceAsync(new WorkflowInstance
        {
            DefinitionId = req.DefinitionId,
            TenantId = tc.TenantId,
            Variables = req.Variables is not null ? JsonSerializer.Serialize(req.Variables) : "{}"
        });
        return Results.Created($"/api/workflows/instances/{instance.Id}",
            new InstanceResponse(instance.Id, instance.DefinitionId, instance.Status, instance.CurrentActivity, instance.CreatedAt, instance.UpdatedAt));
    }

    public static async Task<IResult> UpdateInstance(
        Guid id,
        UpdateInstanceRequest req,
        IWorkflowRepository workflowRepo)
    {
        var instance = await workflowRepo.UpdateInstanceAsync(id, i =>
        {
            if (req.Status is not null) i.Status = req.Status;
            if (req.CurrentActivity is not null) i.CurrentActivity = req.CurrentActivity;
            if (req.Variables is not null) i.Variables = JsonSerializer.Serialize(req.Variables);
        });
        if (instance is null)
            return Results.NotFound(new { error = "Instance not found" });
        return Results.Ok(new InstanceResponse(instance.Id, instance.DefinitionId, instance.Status, instance.CurrentActivity, instance.CreatedAt, instance.UpdatedAt));
    }

    public static async Task<IResult> ListInstances(
        IWorkflowRepository workflowRepo,
        ITenantContext tc,
        Guid? definitionId,
        int? page,
        int? pageSize)
    {
        var (instances, total) = await workflowRepo.ListInstancesAsync(definitionId, tc.TenantId, page ?? 1, pageSize ?? 20);
        return Results.Ok(new
        {
            instances = instances.Select(i =>
                new InstanceResponse(i.Id, i.DefinitionId, i.Status, i.CurrentActivity, i.CreatedAt, i.UpdatedAt)),
            total
        });
    }

    public static async Task<IResult> CancelInstance(Guid id, IWorkflowRepository workflowRepo)
    {
        var instance = await workflowRepo.UpdateInstanceAsync(id, i =>
        {
            i.Status = "cancelled";
            i.CompletedAt = DateTime.UtcNow;
        });
        return instance is not null
            ? Results.Ok(new { message = "Instance cancelled" })
            : Results.NotFound(new { error = "Instance not found" });
    }

    public static async Task<IResult> DeleteInstance(Guid id, IWorkflowRepository workflowRepo)
    {
        var deleted = await workflowRepo.DeleteInstanceAsync(id);
        return deleted
            ? Results.Ok(new { message = "Instance deleted" })
            : Results.NotFound(new { error = "Instance not found" });
    }

    /// <summary>
    /// Returns recent events for a workflow instance.
    ///
    /// <para>Audit finding 016 (cross-tenant leak): the prior version called
    /// <c>QueryAsync(null, ...)</c>, returning events from every tenant. This
    /// version (a) loads the instance and 404s when not found OR when its
    /// tenant doesn't match the ambient <see cref="ITenantContext"/>, and (b)
    /// passes the instance tenant into the repo so the query is scoped
    /// even if the global query filter is disabled.</para>
    /// </summary>
    public static async Task<IResult> GetInstanceEvents(
        Guid id,
        IWorkflowRepository workflowRepo,
        IEventRepository eventRepo,
        ITenantContext tc,
        int? limit)
    {
        var instance = await workflowRepo.GetInstanceAsync(id);
        if (instance is null)
            return Results.NotFound(new { error = "Instance not found" });

        // Cross-tenant access guard: the caller's ambient tenant must own the
        // instance. The ambient null case (system scope) is allowed for
        // self-hosted/admin paths.
        if (tc.TenantId.HasValue && instance.TenantId.HasValue
            && instance.TenantId.Value != tc.TenantId.Value)
        {
            return Results.NotFound(new { error = "Instance not found" });
        }

        var events = await eventRepo.QueryAsync(instance.TenantId, null, null, limit ?? 50);
        return Results.Ok(events.Select(e => new { e.Id, e.Type, e.Data, e.CreatedAt }));
    }
}
