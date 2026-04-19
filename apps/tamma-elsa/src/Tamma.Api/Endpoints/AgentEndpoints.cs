using System.Security.Claims;
using System.Text.Json;
using Tamma.Api.Dtos.Agents;
using Tamma.Api.Services.Agents;
using Tamma.Data;
using Tamma.Data.Repositories;
using Tamma.Data.Entities;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Agent configuration + resolver endpoints.
///
/// <c>GetConfig / UpdateConfig / ValidateConfig</c> manage the raw tenant
/// JSONB override stored in <c>agent_configs</c>. <c>ResolveAgent</c> and
/// <c>ResolveForPhase</c> produce a fully-merged <see cref="ResolvedAgentConfig"/>
/// via <see cref="IAgentResolverService"/>.
/// </summary>
public static class AgentEndpoints
{
    // -----------------------------------------------------------------------
    // Config CRUD — raw tenant override JSON
    // -----------------------------------------------------------------------

    /// <summary>Return the current tenant's agent config (or empty platform default marker).</summary>
    public static async Task<IResult> GetConfig(
        IAgentConfigRepository configRepo,
        ITenantContext tenantContext)
    {
        var config = await configRepo.GetAsync(tenantContext.TenantId);
        if (config is null)
        {
            return Results.Ok(new AgentConfigResponse(new { }, "platform-default", 0));
        }
        return Results.Ok(new AgentConfigResponse(
            JsonSerializer.Deserialize<object>(config.Config) ?? new { },
            "tenant-override",
            config.Version));
    }

    /// <summary>
    /// Upsert the tenant's agent config. Validates schema, increments
    /// version, and appends a domain event for audit.
    /// </summary>
    public static async Task<IResult> UpdateConfig(
        UpdateAgentConfigRequest req,
        IAgentConfigRepository configRepo,
        IEventRepository events,
        ITenantContext tenantContext,
        ClaimsPrincipal principal)
    {
        var configJson = JsonSerializer.Serialize(req.Config);
        // Schema-level validation before write
        var (valid, errors) = ValidateConfigShape(configJson);
        if (!valid)
        {
            return Results.BadRequest(new { valid = false, errors });
        }

        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var userGuid = userId is not null && Guid.TryParse(userId, out var g) ? g : (Guid?)null;

        var saved = await configRepo.UpsertAsync(tenantContext.TenantId, configJson, userGuid);

        // Emit audit event (DCB pattern)
        await events.AppendAsync(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = "AGENT_CONFIG.UPDATED.SUCCESS",
            TenantId = tenantContext.TenantId,
            Tags = JsonSerializer.Serialize(new
            {
                tenantId = tenantContext.TenantId?.ToString(),
                userId = userId,
            }),
            Metadata = JsonSerializer.Serialize(new
            {
                workflowVersion = "1.0.0",
                eventSource = "system",
            }),
            Data = JsonSerializer.Serialize(new
            {
                version = saved.Version,
            }),
            CreatedAt = DateTime.UtcNow,
        });

        return Results.Ok(new AgentConfigResponse(
            JsonSerializer.Deserialize<object>(saved.Config) ?? new { },
            "tenant-override",
            saved.Version));
    }

    /// <summary>
    /// Validate the shape of a proposed config without persisting.
    /// Checks:
    /// <list type="bullet">
    ///   <item>Valid JSON (not malformed).</item>
    ///   <item>Root is an object.</item>
    ///   <item>If <c>roles</c> is present, each entry key is a valid role
    ///         (see <see cref="RolePhaseMap.ValidRoles"/>).</item>
    ///   <item>No forbidden prototype-pollution keys in role names.</item>
    /// </list>
    /// </summary>
    public static IResult ValidateConfig(ValidateConfigRequest req)
    {
        var configJson = JsonSerializer.Serialize(req.Config);
        var (valid, errors) = ValidateConfigShape(configJson);
        return Results.Ok(new { valid, errors });
    }

    // -----------------------------------------------------------------------
    // Resolver endpoints — merged (default + tenant override)
    // -----------------------------------------------------------------------

    /// <summary>
    /// GET <c>/api/v1/agents/{role}/resolve</c> — resolve the full agent
    /// config for a role with tenant override applied.
    /// </summary>
    public static async Task<IResult> ResolveAgent(
        string role,
        IAgentResolverService resolver,
        ITenantContext tenantContext)
    {
        try
        {
            var resolved = await resolver.ResolveAsync(tenantContext.TenantId, role);
            return Results.Ok(resolved);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }

    /// <summary>
    /// POST <c>/api/v1/agents/resolve-for-phase</c> — resolve the config
    /// for a specific (phase, role) pair. Body fields: <c>phase</c>,
    /// <c>role</c>.
    /// </summary>
    public static async Task<IResult> ResolveForPhase(
        ResolveForPhaseRequest req,
        IAgentResolverService resolver,
        ITenantContext tenantContext)
    {
        // The existing DTO uses (Phase, TaskType) where TaskType semantically
        // carries the role. Keeping the record shape backward-compatible.
        var role = string.IsNullOrWhiteSpace(req.TaskType) ? req.Role ?? string.Empty : req.TaskType;
        try
        {
            var resolved = await resolver.ResolveForPhaseAsync(
                tenantContext.TenantId, req.Phase, role);
            return Results.Ok(resolved);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Schema-level validation. Returns (valid, errors). Tolerant of empty
    /// configs (those are valid — they simply fall through to platform
    /// defaults during resolution).
    /// </summary>
    private static (bool Valid, string[] Errors) ValidateConfigShape(string configJson)
    {
        var errors = new List<string>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(configJson);
        }
        catch (JsonException ex)
        {
            return (false, new[] { $"Invalid JSON: {ex.Message}" });
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Root must be a JSON object.");
                return (false, errors.ToArray());
            }

            if (root.TryGetProperty("roles", out var roles))
            {
                if (roles.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("'roles' must be an object.");
                    return (false, errors.ToArray());
                }

                foreach (var prop in roles.EnumerateObject())
                {
                    if (RolePhaseMap.ForbiddenKeys.Contains(prop.Name))
                    {
                        errors.Add($"Forbidden role key: '{prop.Name}'.");
                        continue;
                    }
                    if (RolePhaseMap.ValidRoles.Contains(prop.Name)) continue;
                    // Legacy TS role names — accept and document migration
                    // path rather than 400-ing on existing rows. Finding 001.
                    if (RolePhaseMap.LegacyRoleAliases.ContainsKey(prop.Name)) continue;
                    errors.Add(
                        $"Unknown role '{prop.Name}'. Valid: " +
                        string.Join(", ", RolePhaseMap.ValidRoles) + ".");
                }
            }
        }

        return (errors.Count == 0, errors.ToArray());
    }
}
