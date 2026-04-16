using System.Text.Json;
using Tamma.Api.Dtos.Settings;
using Tamma.Api.Services.Sanitization;
using Tamma.Data;
using Tamma.Data.Entities;
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

    /// <summary>
    /// POST /api/config/sanitize — redact secrets and PII from arbitrary text
    /// using the tenant's effective rule set.
    /// </summary>
    /// <remarks>
    /// Accepts the new <c>{ text, context? }</c> shape. The legacy
    /// <c>{ content }</c> field is still honoured for callers that predate
    /// this rewrite so the cut-over doesn't break anyone.
    /// </remarks>
    public static async Task<IResult> Sanitize(
        SanitizeEndpointRequest req,
        ISanitizationService sanitizer,
        ITenantContext tc)
    {
        var input = req.Text ?? req.Content ?? string.Empty;
        var result = await sanitizer.SanitizeAsync(input, tc.TenantId);
        return Results.Ok(result);
    }

    /// <summary>
    /// GET /api/config/sanitize/rules — return the merged (defaults + tenant
    /// overrides) sanitization rule set.
    /// </summary>
    public static async Task<IResult> GetSanitizationRules(
        ISanitizationRepository repo,
        ITenantContext tc)
    {
        var rules = await repo.GetRulesAsync(tc.TenantId);
        return Results.Ok(rules);
    }

    /// <summary>
    /// PUT /api/config/sanitize/rules — replace the tenant's entire override
    /// set. Rules are validated (non-empty name, parseable regex) before
    /// persistence; invalid patterns are rejected with 400.
    /// </summary>
    public static async Task<IResult> UpdateSanitizationRules(
        UpdateSanitizationRulesRequest req,
        ISanitizationRepository repo,
        ITenantContext tc)
    {
        var rules = ParseRulesRequest(req.Rules);
        if (rules is null)
        {
            return Results.BadRequest(new { error = "Rules must be a JSON array of rule objects." });
        }

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name))
                return Results.BadRequest(new { error = "Every rule requires a non-empty name." });
            if (string.IsNullOrEmpty(rule.Pattern))
                return Results.BadRequest(new { error = $"Rule '{rule.Name}' has empty pattern." });
            try
            {
                _ = new System.Text.RegularExpressions.Regex(rule.Pattern);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = $"Rule '{rule.Name}' pattern invalid: {ex.Message}" });
            }
        }

        await repo.ReplaceRulesAsync(tc.TenantId, rules);
        return Results.Ok(new { message = "Sanitization rules updated", count = rules.Count });
    }

    /// <summary>
    /// Accepts either a strongly-typed <see cref="SanitizationRuleDefinition"/>
    /// array or a JSON-element-boxed version so the minimal-API model binder
    /// stays happy with <c>object</c> payloads.
    /// </summary>
    private static List<SanitizationRuleDefinition>? ParseRulesRequest(object? rules)
    {
        if (rules is null) return new List<SanitizationRuleDefinition>();

        // Most common path: minimal APIs hand us a JsonElement.
        if (rules is JsonElement je)
        {
            if (je.ValueKind != JsonValueKind.Array) return null;
            var opts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
            return je.Deserialize<List<SanitizationRuleDefinition>>(opts)
                ?? new List<SanitizationRuleDefinition>();
        }

        // Fallback: caller already handed us the typed object graph.
        if (rules is IEnumerable<SanitizationRuleDefinition> typed)
        {
            return typed.ToList();
        }

        // Last resort: round-trip through JSON.
        try
        {
            var opts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
            var json = JsonSerializer.Serialize(rules, opts);
            return JsonSerializer.Deserialize<List<SanitizationRuleDefinition>>(json, opts)
                ?? new List<SanitizationRuleDefinition>();
        }
        catch (JsonException)
        {
            return null;
        }
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
