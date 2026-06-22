using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Api.Dtos.Agents;
using Tamma.Core;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Default implementation of <see cref="IAgentResolverService"/> backed by
/// <see cref="IAgentConfigRepository"/>.
///
/// Port of <c>RoleBasedAgentResolver</c> from the deleted TS providers
/// package (Story 9-8), plus the 3-level config merge from
/// <c>ConfigService.ts</c> (Epic 19 Phase 3 deletion).
///
/// <para>Story 32-2 adds the entity-aware resolve methods
/// (<see cref="ResolveForRoleAsync"/> / <see cref="ResolveForRoleAndPhaseAsync"/>)
/// over the Story 32-1 agent entities. The legacy JSONB path
/// (<see cref="ResolveAsync"/> + <see cref="ResolveForPhaseAsync"/>) is
/// UNTOUCHED — the registry/agent collaborators are optional so the legacy
/// constructors (and their tests) keep compiling and running.</para>
/// </summary>
public sealed class AgentResolverService : IAgentResolverService
{
    private readonly IAgentConfigRepository _repo;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<AgentResolverService> _logger;

    // Story 32-2 — entity-aware resolution collaborators. Null on the legacy
    // constructors (the JSONB path never touches them).
    private readonly IAgentRegistryService? _registry;
    private readonly IAgentRepository? _agents;
    private readonly IEventRepository? _events;
    private readonly IMissingConfigRecorder? _missingConfig;

    // Story 32-15 — the persona/public prompt seam (reads Epic 27, fail-loud).
    // The PUBLIC branch of MaterialiseAsync calls this (not an inline prompt
    // store resolve). Null only on the legacy constructors that never reach the
    // public branch.
    private readonly IPersonaPromptResolver? _personaPrompts;

    // Story 32-17 — the custom/private prompt seam (reads the agent's OWN
    // embedded ConfigJson.prompts, fail-loud). The CUSTOM branch of
    // MaterialiseAsync calls this when a private agent carries a non-empty
    // prompts block. Optional so the 32-2/32-15 chain tests can omit it; when a
    // private agent IS committed to the custom branch but the seam is unwired,
    // resolution fails loud (no silent persona/empty fallback).
    private readonly ICustomAgentPromptResolver? _customAgentPrompts;

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

    /// <summary>
    /// Story 32-2 — full constructor wiring the entity-aware resolution chain.
    /// The missing-config recorder is optional (the epic may not be merged).
    /// Story 32-15 adds the <see cref="IPersonaPromptResolver"/> persona/public
    /// prompt seam (optional so the 32-2 chain tests can omit it; the public
    /// branch fails loud if it is reached without the seam wired).
    /// </summary>
    public AgentResolverService(
        IAgentConfigRepository repo,
        IConfiguration? configuration,
        ILogger<AgentResolverService> logger,
        IAgentRegistryService registry,
        IAgentRepository agents,
        IEventRepository events,
        IMissingConfigRecorder? missingConfig = null,
        IPersonaPromptResolver? personaPrompts = null,
        ICustomAgentPromptResolver? customAgentPrompts = null)
        : this(repo, configuration, logger)
    {
        _registry = registry;
        _agents = agents;
        _events = events;
        _missingConfig = missingConfig;
        _personaPrompts = personaPrompts;
        _customAgentPrompts = customAgentPrompts;
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
    // Story 32-2 — entity-aware resolution chain
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<ResolvedAgentConfig> ResolveForRoleAsync(
        string role, CancellationToken ct = default)
        => await ResolveEntityAsync(role, phase: null, ct);

    /// <inheritdoc />
    public async Task<ResolvedAgentConfig> ResolveForRoleAndPhaseAsync(
        string phase, string role, CancellationToken ct = default)
    {
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

        return await ResolveEntityAsync(role, phase, ct);
    }

    /// <summary>
    /// The 4-branch precedence chain (AC 3). NEVER returns an empty/plain
    /// config: the 4th branch emits <c>AGENT.RESOLVE.FAILED</c>, best-effort
    /// records a <c>MISSING_CONFIG</c> gap, then throws.
    /// </summary>
    private async Task<ResolvedAgentConfig> ResolveEntityAsync(
        string role, string? phase, CancellationToken ct)
    {
        role = RolePhaseMap.NormalizeRole(role);
        RolePhaseMap.AssertValidRole(role);

        if (_registry is null || _agents is null || _events is null)
        {
            throw new InvalidOperationException(
                "Entity-aware agent resolution requires the Story 32-2 collaborators "
                + "(IAgentRegistryService / IAgentRepository / IEventRepository). Use the "
                + "full AgentResolverService constructor.");
        }

        // Branches 1+2: the principal's selection (private OR public target).
        var selections = await _registry.GetRoleSelectionsAsync(ct);
        if (selections.TryGetValue(role, out var sel))
        {
            // Recompute provenance at resolve time — a stale/archived/cross-scope
            // target degrades to the system default rather than resolving stale.
            var selected = await _registry.ResolveUsableAgentAsync(sel.AgentId, ct);
            if (selected is { Status: AgentStatus.Active })
            {
                var source = selected.Visibility == AgentVisibility.Public
                    ? "tenant-public"   // principal SELECTED a public agent
                    : "tenant-private"; // principal's own private agent
                var materialised = await MaterialiseAsync(selected, role, phase, source, ct);
                if (materialised is not null)
                {
                    _logger.LogDebug(
                        "agent.resolve.selection role={Role} agentId={AgentId} source={Source}",
                        role, selected.Id, source);
                    return materialised;
                }
            }
            else
            {
                _logger.LogWarning(
                    "agent.resolve.stale_selection role={Role} staleAgentId={StaleAgentId} — degrading to system default",
                    role, sel.AgentId);
            }
        }

        // Branch 3: system-default public PERSONA (Story 32-15 — the configured
        // default persona, role-independent). GetSystemDefaultPublicAsync itself
        // fails loud (AGENT_DEFAULT_PERSONA_MISSING) when the configured persona
        // is not seeded; we treat that as "no system default" so the resolver
        // still emits the mandatory AGENT.RESOLVE.FAILED audit event and throws
        // its canonical no-default error (32-2 AC9 contract preserved).
        Agent? systemDefault = null;
        try
        {
            systemDefault = await _registry.GetSystemDefaultPublicAsync(role, ct);
        }
        catch (TammaError ex) when (ex.Code == "AGENT_DEFAULT_PERSONA_MISSING")
        {
            _logger.LogWarning(
                "agent.resolve.default_persona_missing role={Role} — no configured default persona seeded",
                role);
        }

        if (systemDefault is not null)
        {
            var materialised = await MaterialiseAsync(systemDefault, role, phase, "system-public", ct);
            if (materialised is not null)
            {
                _logger.LogDebug(
                    "agent.resolve.system_default role={Role} agentId={AgentId}",
                    role, systemDefault.Id);
                return materialised;
            }
        }

        // Branch 4: NO empty/plain fallback — fail loud.
        await FailLoudAsync(role, phase, ct);
        // Unreachable — FailLoudAsync always throws.
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// Materialise an agent's ACTIVE version config into a
    /// <see cref="ResolvedAgentConfig"/>, reusing the legacy merge + validation
    /// so <c>CallLlmActivity</c> sees the same shape. The agent's saved-config
    /// JSON is treated as an override on top of the role's platform default;
    /// <see cref="ResolvedAgentConfig.AgentId"/>/<c>AgentVersion</c>/<c>Source</c>
    /// are stamped. Returns <c>null</c> if the agent has no active version (so
    /// the caller can degrade to the next branch).
    /// </summary>
    private async Task<ResolvedAgentConfig?> MaterialiseAsync(
        Agent agent, string role, string? phase, string source, CancellationToken ct)
    {
        var version = await _agents!.GetActiveVersionAsync(agent.Id, ct);
        if (version is null)
        {
            return null;
        }

        var resolved = DefaultAgentConfig.ForRole(role);

        try
        {
            using var doc = JsonDocument.Parse(version.ConfigJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                resolved = MergeOverride(resolved, doc.RootElement);
            }
        }
        catch (JsonException)
        {
            // A corrupt snapshot is a real fault — fall through to validation,
            // which fails loud on the (now-default) required fields if needed.
        }

        // Story 32-15 + 32-17 — the system/role prompt source. The SINGLE
        // documented conditional: a PRIVATE/custom agent with a NON-EMPTY
        // embedded prompts block → its own prompts via the 32-17
        // ICustomAgentPromptResolver seam; everything else (public personas, AND
        // private agents with an empty/absent prompts block) → the Epic 27 store
        // via the 32-15 IPersonaPromptResolver seam. Both fail loud (no
        // empty/plain fallback).
        var (systemPrompt, promptSource) =
            await ResolvePromptSourceAsync(agent, version, role, phase, resolved, ct);

        // The agent's stable handle wins over the merged handle (identity).
        var enriched = new ResolvedAgentConfig
        {
            Role = role,
            Handle = string.IsNullOrWhiteSpace(agent.Name) ? resolved.Handle : agent.Name,
            Provider = resolved.Provider,
            Model = resolved.Model,
            Temperature = resolved.Temperature,
            MaxTokens = resolved.MaxTokens,
            TokenBudget = resolved.TokenBudget,
            Tools = resolved.Tools,
            SystemPrompt = systemPrompt,
            Source = source,
            Phase = phase,
            MaxBudgetUsd = resolved.MaxBudgetUsd,
            PermissionMode = resolved.PermissionMode,
            AllowedTools = resolved.AllowedTools,
            AgentId = agent.Id,
            AgentVersion = version.Version,
            PromptSource = promptSource,
        };

        ValidateResolved(enriched);
        return enriched;
    }

    /// <summary>
    /// Story 32-15 + 32-17 — resolve the system/role prompt for the materialised
    /// config via the SINGLE documented prompt-source conditional. Returns the
    /// resolved prompt text plus its <see cref="AgentPromptSource"/> provenance.
    ///
    /// <para>The ONE conditional (Epic 32 §3.2): a PRIVATE agent carrying a
    /// NON-EMPTY embedded <c>ConfigJson.prompts</c> block is a CUSTOM agent — its
    /// prompt is sourced from its own prompts via the 32-17
    /// <see cref="ICustomAgentPromptResolver"/> seam
    /// (<c>byRoleAction → system → ERROR</c>). EVERYTHING ELSE — public personas
    /// AND private agents with an empty/absent prompts block — flows to the
    /// persona branch via the 32-15 <see cref="IPersonaPromptResolver"/> seam
    /// (→ Epic 27 store). Both legs fail loud, never empty/plain; this story owns
    /// only the custom leg + the selector, never the Epic 27 leg.</para>
    /// </summary>
    private async Task<(string SystemPrompt, AgentPromptSource Source)> ResolvePromptSourceAsync(
        Agent agent, AgentVersion version, string role, string? action,
        ResolvedAgentConfig merged, CancellationToken ct)
    {
        // The discriminator: a private agent's OWN non-empty prompts commit it to
        // the custom branch. (A public persona never carries prompts — the
        // validator rejects that — and an empty/absent block delegates to persona.)
        var promptSet = agent.Visibility == AgentVisibility.Private
            ? AgentPromptSet.TryRead(version.ConfigJson)
            : null;

        if (promptSet is { IsEmpty: false })
        {
            // ── CUSTOM / PRIVATE branch (Story 32-17, via ICustomAgentPromptResolver) ──
            //    byRoleAction["<role>:<action>"] → system → ERROR. The seam fails
            //    loud (CustomPromptUnresolvedException) — NEVER empty/plain, NEVER
            //    fall through to Epic 27.
            _logger.LogDebug(
                "agent.materialise.prompt_source branch=custom-agent agentId={AgentId} role={Role} action={Action}",
                agent.Id, role, action ?? "(role-system)");

            if (_customAgentPrompts is null)
            {
                throw new InvalidOperationException(
                    "A custom (private) agent with embedded prompts must resolve via "
                    + "ICustomAgentPromptResolver (Story 32-17), but no resolver is wired. "
                    + "Use the full AgentResolverService constructor with the custom prompt seam.");
            }

            var customPrompt = await _customAgentPrompts.ResolveAsync(agent, role, action, ct);
            return (customPrompt, AgentPromptSource.CustomAgent);
        }

        // ── PERSONA / PUBLIC branch (Story 32-15, via IPersonaPromptResolver) ──
        //    persona/public + empty-prompts private → Epic 27 store
        //    (principal, role, action). This story does NOT implement this leg.
        _logger.LogDebug(
            "agent.materialise.prompt_source branch=epic27-store agentId={AgentId} visibility={Visibility} role={Role} action={Action}",
            agent.Id, agent.Visibility, role, action ?? "(role-system)");

        if (_personaPrompts is null)
        {
            throw new InvalidOperationException(
                "A persona prompt must be resolved via IPersonaPromptResolver "
                + "(Story 32-15), but no resolver is wired. Use the full "
                + "AgentResolverService constructor with the persona prompt seam.");
        }
        var (tenantId, userId) = _registry!.ResolvePrincipal();
        var principal = new Principal(tenantId, userId);
        // The entity-aware resolve path is role-based (no Epic 27 action), so we
        // resolve the role-system (identity preamble) prompt; the seam is
        // fail-loud internally (PROMPT_UNRESOLVED) — no empty/plain fallback.
        var personaPrompt = await _personaPrompts.ResolveAsync(principal, role, action: null, ct);
        return (personaPrompt, AgentPromptSource.Epic27Store);
    }

    /// <summary>
    /// AC 9 — the no-empty-fallback path. Emits <c>AGENT.RESOLVE.FAILED</c>
    /// (mandatory), best-effort records a <c>MISSING_CONFIG</c> gap (optional),
    /// then throws <see cref="TammaError"/>. Mirrors
    /// <c>PromptStoreService.NoPromptError</c> / <c>ConventionStore</c>.
    /// </summary>
    private async Task FailLoudAsync(string role, string? phase, CancellationToken ct)
    {
        var (tenantId, _) = _registry!.ResolvePrincipal();
        var tags = new Dictionary<string, object?>
        {
            ["role"] = role,
            ["phase"] = phase,
            ["source"] = "none",
            ["mode"] = tenantId is not null ? "saas" : "single-user",
        };

        await _events!.AppendAsync(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = AgentEventTypes.ResolveFailed,
            // A missing SYSTEM default is platform-scope (TenantId null); a
            // tenant-scope resolve carries the ambient tenant.
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = JsonSerializer.Serialize(new
            {
                workflowVersion = "1.0.0",
                eventSource = "system",
            }),
            Data = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["role"] = role,
                ["phase"] = phase,
            }),
            CreatedAt = DateTime.UtcNow,
        });

        if (_missingConfig is not null)
        {
            try
            {
                await _missingConfig.RecordAsync(
                    domain: "agent",
                    configKey: $"role:{role}",
                    scope: "system",
                    context: new Dictionary<string, object?> { ["role"] = role, ["phase"] = phase },
                    ct);
            }
            catch (Exception ex)
            {
                // Best-effort — a recorder failure must not mask the TammaError.
                _logger.LogWarning(ex,
                    "agent.resolve.missing_config_record_failed role={Role}", role);
            }
        }

        _logger.LogError(
            "AGENT.RESOLVE.FAILED role={Role} phase={Phase} — no agent resolvable", role, phase);

        throw new TammaError(
            "AGENT.RESOLVE.NO_DEFAULT",
            $"No agent resolvable for role '{role}': no selection and no system-default public agent. "
            + "Resolution is private-selection → public-selection → system-default → error; "
            + "there is no empty/plain fallback.",
            new Dictionary<string, object?> { ["role"] = role, ["phase"] = phase },
            retryable: false,
            severity: TammaErrorSeverity.High);
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
