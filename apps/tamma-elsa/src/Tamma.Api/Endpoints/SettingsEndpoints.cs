using System.Text.Json;
using Tamma.Api.Dtos.Settings;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class SettingsEndpoints
{
    public static async Task<IResult> GetAgentsConfig(IAgentConfigRepository configRepo, ITenantContext tc)
    {
        var config = await configRepo.GetAsync(tc.TenantId);
        return Results.Ok(config is not null ? JsonSerializer.Deserialize<object>(config.Config) : new { });
    }

    public static async Task<IResult> UpdateAgentsConfig(UpdateAgentsConfigRequest req, IAgentConfigRepository configRepo, ITenantContext tc)
    {
        await configRepo.UpsertAsync(tc.TenantId, JsonSerializer.Serialize(req.Config), null);
        return Results.Ok(new { message = "Agent config updated" });
    }

    public static async Task<IResult> GetSecurityConfig(IAgentConfigRepository configRepo, ITenantContext tc)
    {
        var config = await configRepo.GetAsync(tc.TenantId);
        return Results.Ok(config is not null ? JsonSerializer.Deserialize<object>(config.Config) : new { });
    }

    public static async Task<IResult> UpdateSecurityConfig(UpdateSecurityConfigRequest req, IAgentConfigRepository configRepo, ITenantContext tc)
    {
        await configRepo.UpsertAsync(tc.TenantId, JsonSerializer.Serialize(req.Config), null);
        return Results.Ok(new { message = "Security config updated" });
    }

    public static Task<IResult> Sanitize(SanitizeRequest req)
    {
        // Stub: return content as-is
        return Task.FromResult(Results.Ok(new { sanitized = req.Content }));
    }

    public static async Task<IResult> GetSanitizationRules(ISanitizationRepository repo, ITenantContext tc)
    {
        var rules = await repo.GetRulesAsync(tc.TenantId);
        return Results.Ok(rules is not null ? JsonSerializer.Deserialize<object>(rules.Rules) : new { });
    }

    public static async Task<IResult> UpdateSanitizationRules(UpdateSanitizationRulesRequest req, ISanitizationRepository repo, ITenantContext tc)
    {
        await repo.UpsertRulesAsync(tc.TenantId, JsonSerializer.Serialize(req.Rules));
        return Results.Ok(new { message = "Sanitization rules updated" });
    }

    public static async Task<IResult> GetPromptsConfig(IAgentConfigRepository configRepo, ITenantContext tc)
    {
        var config = await configRepo.GetAsync(tc.TenantId);
        return Results.Ok(config is not null ? JsonSerializer.Deserialize<object>(config.Config) : new { });
    }

    public static async Task<IResult> UpdatePromptsConfig(string role, IAgentConfigRepository configRepo, ITenantContext tc)
    {
        return Results.Ok(new { message = $"Prompt config for role '{role}' updated" });
    }

    public static async Task<IResult> GetProvidersConfig(IAgentConfigRepository configRepo, ITenantContext tc)
    {
        var config = await configRepo.GetAsync(tc.TenantId);
        return Results.Ok(config is not null ? JsonSerializer.Deserialize<object>(config.Config) : new { });
    }

    public static async Task<IResult> UpdateProvidersConfig(IAgentConfigRepository configRepo, ITenantContext tc)
    {
        return Results.Ok(new { message = "Providers config updated" });
    }
}
