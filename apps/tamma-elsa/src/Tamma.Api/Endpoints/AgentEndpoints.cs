using System.Security.Claims;
using System.Text.Json;
using Tamma.Api.Dtos.Agents;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class AgentEndpoints
{
    public static async Task<IResult> GetConfig(
        IAgentConfigRepository configRepo,
        ITenantContext tenantContext)
    {
        var config = await configRepo.GetAsync(tenantContext.TenantId);
        if (config is null)
            return Results.Ok(new AgentConfigResponse(new { }, "default", 0));
        return Results.Ok(new AgentConfigResponse(
            JsonSerializer.Deserialize<object>(config.Config) ?? new { },
            "tenant",
            config.Version));
    }

    public static async Task<IResult> UpdateConfig(
        UpdateAgentConfigRequest req,
        IAgentConfigRepository configRepo,
        ITenantContext tenantContext,
        ClaimsPrincipal principal)
    {
        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var config = await configRepo.UpsertAsync(
            tenantContext.TenantId,
            JsonSerializer.Serialize(req.Config),
            userId is not null ? Guid.Parse(userId) : null);
        return Results.Ok(new AgentConfigResponse(
            JsonSerializer.Deserialize<object>(config.Config) ?? new { },
            "tenant",
            config.Version));
    }

    public static Task<IResult> ValidateConfig(ValidateConfigRequest req)
    {
        // Basic validation stub
        return Task.FromResult(Results.Ok(new { valid = true, errors = Array.Empty<string>() }));
    }

    public static async Task<IResult> ResolveAgent(
        string role,
        IAgentConfigRepository configRepo,
        ITenantContext tenantContext)
    {
        if (!tenantContext.TenantId.HasValue)
            return Results.Ok(new ResolvedAgentResponse("anthropic", "claude-sonnet-4-20250514", new { }));

        var (config, source) = await configRepo.ResolveAsync(tenantContext.TenantId.Value);
        return Results.Ok(new ResolvedAgentResponse("anthropic", "claude-sonnet-4-20250514",
            JsonSerializer.Deserialize<object>(config.Config) ?? new { }));
    }

    public static async Task<IResult> ResolveForPhase(
        ResolveForPhaseRequest req,
        IAgentConfigRepository configRepo,
        ITenantContext tenantContext)
    {
        if (!tenantContext.TenantId.HasValue)
            return Results.Ok(new ResolvedAgentResponse("anthropic", "claude-sonnet-4-20250514", new { }));

        var (config, source) = await configRepo.ResolveAsync(tenantContext.TenantId.Value);
        return Results.Ok(new ResolvedAgentResponse("anthropic", "claude-sonnet-4-20250514",
            JsonSerializer.Deserialize<object>(config.Config) ?? new { }));
    }
}
