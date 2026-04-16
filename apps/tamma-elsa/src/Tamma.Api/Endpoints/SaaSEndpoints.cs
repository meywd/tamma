using System.Text.Json;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class SaaSEndpoints
{
    public static Task<IResult> LlmChat(HttpContext context) =>
        Task.FromResult(Results.Ok(new { message = "LLM chat proxy - stub" }));

    public static async Task<IResult> UpdateWorkflowStatus(
        Guid id,
        IWorkflowRepository workflowRepo)
    {
        var instance = await workflowRepo.UpdateInstanceAsync(id, i => { i.UpdatedAt = DateTime.UtcNow; });
        return instance is not null
            ? Results.Ok(new { message = "Status updated" })
            : Results.NotFound(new { error = "Instance not found" });
    }

    public static async Task<IResult> PostWorkflowResult(
        Guid id,
        IWorkflowRepository workflowRepo,
        IEventRepository eventRepo)
    {
        var instance = await workflowRepo.UpdateInstanceAsync(id, i =>
        {
            i.Status = "completed";
            i.CompletedAt = DateTime.UtcNow;
        });
        return instance is not null
            ? Results.Ok(new { message = "Result recorded" })
            : Results.NotFound(new { error = "Instance not found" });
    }

    public static async Task<IResult> RotateInstallationKey(Guid id, IApiKeyRepository apiKeyRepo)
    {
        // Stub: would rotate the installation's API key
        return Results.Ok(new { message = "Key rotation stub", installationId = id });
    }
}
