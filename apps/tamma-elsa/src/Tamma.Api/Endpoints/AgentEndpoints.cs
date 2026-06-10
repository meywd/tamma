using System.Security.Claims;
using Tamma.Api.Auth;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tamma.Api.Dtos.Agents;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Security;
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
    ///
    /// <para>
    /// Story 28-1 PR A (Decision #1): writes without a tenant context are
    /// rejected with 400. Platform defaults moved to code
    /// (<c>DefaultAgentConfig.ForRole</c>); the legacy "edit the platform
    /// default by PUTing with a null tenant" behaviour was a no-op that
    /// silently dropped the request AND emitted a false success audit
    /// event. Both lies are gone now: callers see an explicit 400 and no
    /// <c>AGENT_CONFIG.UPDATED.SUCCESS</c> hits the event store.
    /// </para>
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

        // Story 28-1 PR A: short-circuit before persistence + audit so we
        // don't poison the DCB stream with a SUCCESS event for a write that
        // never happened. Platform defaults are immutable from this surface.
        if (tenantContext.TenantId is null)
        {
            return Results.BadRequest(new
            {
                error = "no_tenant_context",
                detail = "PUT /api/v1/agents/config requires tenant context; " +
                         "platform defaults are immutable from this endpoint. " +
                         "Edit DefaultAgentConfig.ForRole in code instead.",
            });
        }

        var userGuid = principal.GetUserId();

        var saved = await configRepo.UpsertAsync(tenantContext.TenantId, configJson, userGuid);

        // Emit audit event (DCB pattern). Reachable only after a real write
        // — every emitted event corresponds to a state transition.
        await events.AppendAsync(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = "AGENT_CONFIG.UPDATED.SUCCESS",
            TenantId = tenantContext.TenantId,
            Tags = JsonSerializer.Serialize(new
            {
                tenantId = tenantContext.TenantId?.ToString(),
                userId = userGuid,
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
                tenantContext.TenantId, req.Phase, role, req.TaskOverrides);
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
    /// Provider name regex from Story 9-1 AC 6 / TS validateAgentsConfig:
    /// <c>^[a-z0-9][a-z0-9_-]{0,63}$</c>.
    /// </summary>
    private static readonly Regex ProviderNameRegex =
        new("^[a-z0-9][a-z0-9_-]{0,63}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Schema- AND semantic-level validation. Returns (valid, errors).
    /// Tolerant of empty configs (valid — fall through to platform defaults).
    /// Finding 014: enforces provider regex, budget range [0,100], ReDoS
    /// guard on blockedCommandPatterns, maxFetchSizeBytes range [0, 1 GiB],
    /// and prototype-pollution rejection on every key.
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

            // ── Roles ────────────────────────────────────────────────────────
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
                    var roleKnown = RolePhaseMap.ValidRoles.Contains(prop.Name) ||
                                    RolePhaseMap.LegacyRoleAliases.ContainsKey(prop.Name);
                    if (!roleKnown)
                    {
                        errors.Add(
                            $"Unknown role '{prop.Name}'. Valid: " +
                            string.Join(", ", RolePhaseMap.ValidRoles) + ".");
                        continue;
                    }
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;

                    ValidateRoleSemantics(prop.Name, prop.Value, errors);
                }
            }

            // ── defaults.providerChain (legacy TS shape) ────────────────────
            if (root.TryGetProperty("defaults", out var defaults) &&
                defaults.ValueKind == JsonValueKind.Object &&
                defaults.TryGetProperty("providerChain", out var defChain))
            {
                ValidateProviderChain("defaults.providerChain", defChain, errors);
            }

            // ── chains (canonical 2D shape) ─────────────────────────────────
            if (root.TryGetProperty("chains", out var chains) &&
                chains.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in chains.EnumerateObject())
                {
                    if (RolePhaseMap.ForbiddenKeys.Contains(prop.Name))
                    {
                        errors.Add($"Forbidden chain key: '{prop.Name}'.");
                        continue;
                    }
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        ValidateProviderChain($"chains.{prop.Name}", prop.Value, errors);
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var actionProp in prop.Value.EnumerateObject())
                        {
                            if (actionProp.Value.ValueKind != JsonValueKind.Array) continue;
                            ValidateProviderChain(
                                $"chains.{prop.Name}.{actionProp.Name}",
                                actionProp.Value, errors);
                        }
                    }
                }
            }

            // ── security branch (blockedCommandPatterns + maxFetchSizeBytes) ─
            if (root.TryGetProperty("security", out var security) &&
                security.ValueKind == JsonValueKind.Object)
            {
                ValidateSecurity(security, errors);
            }
        }

        return (errors.Count == 0, errors.ToArray());
    }

    private static void ValidateRoleSemantics(string role, JsonElement obj, List<string> errors)
    {
        // provider name regex
        if (obj.TryGetProperty("provider", out var prov) &&
            prov.ValueKind == JsonValueKind.String)
        {
            var name = prov.GetString() ?? string.Empty;
            if (!ProviderNameRegex.IsMatch(name))
            {
                errors.Add(
                    $"roles.{role}.provider '{name}' must match /^[a-z0-9][a-z0-9_-]{{0,63}}$/.");
            }
        }

        // maxBudgetUsd range [0, 100], finite
        if (obj.TryGetProperty("maxBudgetUsd", out var budget) &&
            budget.ValueKind == JsonValueKind.Number)
        {
            if (!budget.TryGetDouble(out var budgetVal) || double.IsNaN(budgetVal) ||
                double.IsInfinity(budgetVal))
            {
                errors.Add($"roles.{role}.maxBudgetUsd must be a finite number.");
            }
            else if (budgetVal < 0 || budgetVal > 100)
            {
                errors.Add($"roles.{role}.maxBudgetUsd must be in [0, 100] (got {budgetVal}).");
            }
        }

        // permissionMode whitelist
        if (obj.TryGetProperty("permissionMode", out var mode) &&
            mode.ValueKind == JsonValueKind.String)
        {
            var modeVal = mode.GetString();
            if (modeVal is not ("default" or "acceptEdits" or "bypassPermissions"))
            {
                errors.Add(
                    $"roles.{role}.permissionMode must be one of " +
                    "default | acceptEdits | bypassPermissions.");
            }
        }

        // providerChain shape
        if (obj.TryGetProperty("providerChain", out var chain) &&
            chain.ValueKind == JsonValueKind.Array)
        {
            ValidateProviderChain($"roles.{role}.providerChain", chain, errors);
        }
    }

    private static void ValidateProviderChain(string label, JsonElement arr, List<string> errors)
    {
        if (arr.GetArrayLength() == 0)
        {
            errors.Add($"{label}: chain must not be empty.");
            return;
        }
        var i = 0;
        foreach (var entry in arr.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{label}[{i}]: entry must be an object.");
                i++;
                continue;
            }
            if (!entry.TryGetProperty("provider", out var prov) ||
                prov.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{label}[{i}]: missing 'provider' string field.");
                i++;
                continue;
            }
            var name = prov.GetString() ?? string.Empty;
            if (!ProviderNameRegex.IsMatch(name))
            {
                errors.Add(
                    $"{label}[{i}].provider '{name}' must match " +
                    "/^[a-z0-9][a-z0-9_-]{0,63}$/.");
            }
            i++;
        }
    }

    private static void ValidateSecurity(JsonElement sec, List<string> errors)
    {
        if (sec.TryGetProperty("maxFetchSizeBytes", out var fetch))
        {
            if (fetch.ValueKind != JsonValueKind.Number ||
                !fetch.TryGetInt64(out var bytes))
            {
                errors.Add("security.maxFetchSizeBytes must be a number.");
            }
            else if (bytes < 0 || bytes > 1L * 1024 * 1024 * 1024)
            {
                errors.Add(
                    $"security.maxFetchSizeBytes must be in [0, 1 GiB] (got {bytes}).");
            }
        }

        if (sec.TryGetProperty("blockedCommandPatterns", out var patterns) &&
            patterns.ValueKind == JsonValueKind.Array)
        {
            if (patterns.GetArrayLength() > ReDosGuard.MaxPatternCount)
            {
                errors.Add(
                    $"security.blockedCommandPatterns count {patterns.GetArrayLength()} " +
                    $"exceeds max {ReDosGuard.MaxPatternCount}.");
            }
            var i = 0;
            foreach (var entry in patterns.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                {
                    errors.Add($"security.blockedCommandPatterns[{i}]: must be a string.");
                    i++;
                    continue;
                }
                try
                {
                    ReDosGuard.Validate(
                        $"security.blockedCommandPatterns[{i}]",
                        entry.GetString() ?? string.Empty);
                }
                catch (ArgumentException ex)
                {
                    errors.Add(ex.Message);
                }
                i++;
            }
        }
    }
}
