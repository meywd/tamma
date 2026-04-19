using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Api.Dtos.Agents;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Default implementation of <see cref="IAgentResolverService"/> backed by
/// <see cref="IAgentConfigRepository"/>.
///
/// Port of <c>RoleBasedAgentResolver</c> from the deleted TS providers
/// package (Story 9-8), plus the 3-level config merge from
/// <c>ConfigService.ts</c> (Epic 19 Phase 3 deletion).
/// </summary>
public sealed class AgentResolverService : IAgentResolverService
{
    private readonly IAgentConfigRepository _repo;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<AgentResolverService> _logger;

    public AgentResolverService(
        IAgentConfigRepository repo,
        ILogger<AgentResolverService> logger)
        : this(repo, null, logger) { }

    public AgentResolverService(
        IAgentConfigRepository repo,
        IConfiguration? configuration,
        ILogger<AgentResolverService> logger)
    {
        _repo = repo;
        _configuration = configuration;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<ResolvedAgentConfig> ResolveAsync(Guid? tenantId, string role)
    {
        // Translate any legacy TS role identifier (implementer, reviewer, …)
        // before strict validation. Finding 001.
        role = RolePhaseMap.NormalizeRole(role);
        RolePhaseMap.AssertValidRole(role);

        // 1) Platform default (fresh mutable copy)
        var resolved = DefaultAgentConfig.ForRole(role);

        // 2) Tenant override (if any)
        if (tenantId.HasValue)
        {
            var tenantConfig = await _repo.GetTenantConfigAsync(tenantId);
            if (tenantConfig is not null && TryGetRoleOverride(tenantConfig, role, out var roleOverride))
            {
                resolved = MergeOverride(resolved, roleOverride);
            }
        }

        // 3) Validate
        ValidateResolved(resolved);
        return resolved;
    }

    /// <inheritdoc />
    public Task<ResolvedAgentConfig> ResolveForPhaseAsync(
        Guid? tenantId, string phase, string role)
        => ResolveForPhaseAsync(tenantId, phase, role, overrides: null);

    /// <inheritdoc />
    public async Task<ResolvedAgentConfig> ResolveForPhaseAsync(
        Guid? tenantId, string phase, string role, TaskOverrides? overrides)
    {
        // Legacy alias normalisation runs before strict validation so
        // workflows still emitting CODE_GENERATION / implementer keep working
        // through the migration window. Finding 001.
        phase = RolePhaseMap.NormalizePhase(phase);
        role = RolePhaseMap.NormalizeRole(role);
        RolePhaseMap.AssertValidPhase(phase);
        RolePhaseMap.AssertValidRole(role);

        if (!RolePhaseMap.IsRoleEligibleForPhase(phase, role))
        {
            throw new ArgumentException(
                $"Role '{role}' is not eligible for phase '{phase}'. Eligible roles: " +
                string.Join(", ", RolePhaseMap.GetEligibleRolesForPhase(phase)) + ".",
                nameof(role));
        }

        var resolved = await ResolveAsync(tenantId, role);

        // Apply task-override clamping (finding 007). Ceiling always wins:
        // budget can only shrink, tool lists can only narrow, bypass-perm
        // mode requires operator consent via env/config.
        var clampedBudget = resolved.MaxBudgetUsd;
        var clampedTools = resolved.Tools;
        var clampedPermissionMode = resolved.PermissionMode;
        var appliedModel = resolved.Model;

        if (overrides is not null)
        {
            // Budget clamp — Math.Min against role ceiling.
            if (overrides.MaxBudgetUsd.HasValue)
            {
                clampedBudget = clampedBudget.HasValue
                    ? Math.Min(overrides.MaxBudgetUsd.Value, clampedBudget.Value)
                    : overrides.MaxBudgetUsd.Value;
            }

            // Tool intersection — start from role list, drop anything not on
            // it regardless of what the override requested.
            if (overrides.AllowedTools is not null)
            {
                if (resolved.Tools.Count > 0)
                {
                    var roleSet = new HashSet<string>(resolved.Tools, StringComparer.Ordinal);
                    clampedTools = overrides.AllowedTools
                        .Where(t => roleSet.Contains(t))
                        .ToArray();
                }
                else
                {
                    // Role has no tool list → start from what the override offers.
                    clampedTools = overrides.AllowedTools.ToArray();
                }
            }

            // bypassPermissions requires operator consent via env / config.
            // TAMMA_ALLOW_BYPASS_PERMISSIONS takes precedence so ops teams
            // can lock it down without redeploying; the IConfiguration
            // fallback lets staging flip it via appsettings.json.
            if (!string.IsNullOrEmpty(overrides.PermissionMode))
            {
                if (string.Equals(overrides.PermissionMode, "bypassPermissions",
                        StringComparison.Ordinal))
                {
                    var envAllow = Environment.GetEnvironmentVariable(
                        "TAMMA_ALLOW_BYPASS_PERMISSIONS");
                    var cfgAllow = _configuration?["Tamma:AllowBypassPermissions"];
                    var allowed =
                        string.Equals(envAllow, "true", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(cfgAllow, "true", StringComparison.OrdinalIgnoreCase);
                    if (allowed)
                    {
                        clampedPermissionMode = "bypassPermissions";
                    }
                    else
                    {
                        _logger.LogWarning(
                            "bypassPermissions requested for role={Role} phase={Phase} " +
                            "but TAMMA_ALLOW_BYPASS_PERMISSIONS is not set — silently " +
                            "keeping role-level permissionMode",
                            role, phase);
                    }
                }
                else
                {
                    clampedPermissionMode = overrides.PermissionMode;
                }
            }

            if (!string.IsNullOrEmpty(overrides.Model))
            {
                appliedModel = overrides.Model;
            }
        }

        return new ResolvedAgentConfig
        {
            Role = resolved.Role,
            Handle = resolved.Handle,
            Provider = resolved.Provider,
            Model = appliedModel,
            Temperature = resolved.Temperature,
            MaxTokens = resolved.MaxTokens,
            TokenBudget = resolved.TokenBudget,
            Tools = clampedTools,
            SystemPrompt = resolved.SystemPrompt,
            Source = resolved.Source,
            Phase = phase,
            MaxBudgetUsd = clampedBudget,
            PermissionMode = clampedPermissionMode,
            AllowedTools = clampedTools,
        };
    }

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------

    /// <summary>
    /// Look up the per-role object inside a tenant config JsonDocument.
    /// Expected shape: <c>{ "roles": { "developer": { ... }, ... } }</c>.
    /// Falls back to legacy TS role keys when the canonical key isn't
    /// present, so a row written as <c>roles.implementer</c> still resolves
    /// for caller asking for <c>developer</c>. Finding 001.
    /// </summary>
    private static bool TryGetRoleOverride(
        JsonDocument doc, string role, out JsonElement roleOverride)
    {
        roleOverride = default;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (!root.TryGetProperty("roles", out var roles) ||
            roles.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        // Try the canonical key first.
        if (roles.TryGetProperty(role, out var value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            roleOverride = value;
            return true;
        }
        // Walk legacy aliases that map to the requested canonical role.
        foreach (var (legacy, canonical) in RolePhaseMap.LegacyRoleAliases)
        {
            if (!string.Equals(canonical, role, StringComparison.OrdinalIgnoreCase))
                continue;
            if (roles.TryGetProperty(legacy, out var legacyValue) &&
                legacyValue.ValueKind == JsonValueKind.Object)
            {
                roleOverride = legacyValue;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Merge a JSON role override on top of the platform default. Any field
    /// present in the override replaces the default; absent fields are kept.
    /// Marks the resulting <see cref="ResolvedAgentConfig.Source"/> as
    /// <c>"tenant-override"</c>.
    /// </summary>
    private static ResolvedAgentConfig MergeOverride(
        ResolvedAgentConfig baseConfig, JsonElement roleOverride)
    {
        var provider = GetStringOrDefault(roleOverride, "provider", baseConfig.Provider);
        var model = GetStringOrDefault(roleOverride, "model", baseConfig.Model);
        var temperature = GetDoubleOrDefault(roleOverride, "temperature", baseConfig.Temperature);
        var maxTokens = GetIntOrDefault(roleOverride, "maxTokens", baseConfig.MaxTokens);
        var tokenBudget = GetIntOrDefault(roleOverride, "tokenBudget", baseConfig.TokenBudget);
        var systemPrompt = GetStringOrDefault(roleOverride, "systemPrompt", baseConfig.SystemPrompt);
        var handle = GetStringOrDefault(roleOverride, "handle", baseConfig.Handle);
        var tools = GetStringArrayOrDefault(roleOverride, "tools", baseConfig.Tools);

        return new ResolvedAgentConfig
        {
            Role = baseConfig.Role,
            Handle = handle,
            Provider = provider,
            Model = model,
            Temperature = temperature,
            MaxTokens = maxTokens,
            TokenBudget = tokenBudget,
            Tools = tools,
            SystemPrompt = systemPrompt,
            Source = "tenant-override",
        };
    }

    /// <summary>
    /// Assert required fields are present and non-empty after merge.
    /// Guards against malformed JSON overrides (empty strings, missing keys
    /// that silently elided real values).
    /// </summary>
    private static void ValidateResolved(ResolvedAgentConfig r)
    {
        if (string.IsNullOrWhiteSpace(r.Provider))
        {
            throw new InvalidOperationException(
                $"Resolved config for role '{r.Role}' is missing required field 'provider'.");
        }
        if (string.IsNullOrWhiteSpace(r.Model))
        {
            throw new InvalidOperationException(
                $"Resolved config for role '{r.Role}' is missing required field 'model'.");
        }
        if (string.IsNullOrWhiteSpace(r.Handle))
        {
            throw new InvalidOperationException(
                $"Resolved config for role '{r.Role}' is missing required field 'handle'.");
        }
        if (r.MaxTokens <= 0)
        {
            throw new InvalidOperationException(
                $"Resolved config for role '{r.Role}' has non-positive 'maxTokens'.");
        }
        if (r.TokenBudget <= 0)
        {
            throw new InvalidOperationException(
                $"Resolved config for role '{r.Role}' has non-positive 'tokenBudget'.");
        }
    }

    // -----------------------------------------------------------------------
    // JSON extraction helpers
    // -----------------------------------------------------------------------

    private static string GetStringOrDefault(
        JsonElement obj, string key, string fallback)
    {
        if (!obj.TryGetProperty(key, out var el))
        {
            return fallback;
        }
        // An explicit empty string or null is considered "present" and
        // overrides the default — validation will catch this if it violates
        // a required-field constraint.
        if (el.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }
        return el.GetString() ?? string.Empty;
    }

    private static double GetDoubleOrDefault(
        JsonElement obj, string key, double fallback)
    {
        if (!obj.TryGetProperty(key, out var el) ||
            el.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }
        return el.GetDouble();
    }

    private static int GetIntOrDefault(
        JsonElement obj, string key, int fallback)
    {
        if (!obj.TryGetProperty(key, out var el) ||
            el.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }
        return el.GetInt32();
    }

    private static IReadOnlyList<string> GetStringArrayOrDefault(
        JsonElement obj, string key, IReadOnlyList<string> fallback)
    {
        if (!obj.TryGetProperty(key, out var el) ||
            el.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }
        var result = new List<string>(el.GetArrayLength());
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    result.Add(s);
                }
            }
        }
        return result;
    }
}
