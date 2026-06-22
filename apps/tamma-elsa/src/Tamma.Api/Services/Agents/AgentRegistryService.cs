using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamma.Api.Auth;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Default <see cref="IAgentRegistryService"/>. Layers selection + system-default
/// resolution over the Story 32-1 <see cref="IAgentRepository"/> (CP-resident
/// agent identities) and the dual-scoped <see cref="IAgentSelectionRepository"/>
/// (CP for single-user, tenant schema for SaaS). The process mode
/// (<see cref="ITammaModeProvider"/>) settles the principal: SaaS ⇒ ambient
/// tenant id; single-user ⇒ the calling user id.
/// </summary>
public sealed class AgentRegistryService : IAgentRegistryService
{
    private const string WorkflowVersion = "1.0.0";

    private readonly IAgentRepository _agents;
    private readonly IAgentSelectionRepository _selections;
    private readonly IEventRepository _events;
    private readonly ITammaModeProvider _mode;
    private readonly ITenantContext _tenantContext;
    private readonly IHttpContextAccessor _httpContext;
    private readonly DefaultPersonaOptions _defaultPersona;

    // Story 32-18 — the per-tenant enablement READ seam (32-16). The usability
    // gate (CanUseAsync / SelectForRoleAsync / GetSystemDefaultPublicAsync)
    // consumes it so a public persona is usable ONLY when enabled for the
    // principal. Optional so the legacy 32-2/32-15 test harnesses (which predate
    // this gate) keep constructing; production ALWAYS wires it (DI). When null,
    // the gate degrades to the pre-32-18 "any public is usable" behaviour — used
    // only by tests that don't exercise enablement.
    private readonly ITenantAgentEnablementReader? _enablement;

    private readonly ILogger<AgentRegistryService>? _logger;

    public AgentRegistryService(
        IAgentRepository agents,
        IAgentSelectionRepository selections,
        IEventRepository events,
        ITammaModeProvider mode,
        ITenantContext tenantContext,
        IHttpContextAccessor httpContext,
        IOptions<DefaultPersonaOptions>? defaultPersona = null,
        ITenantAgentEnablementReader? enablement = null,
        ILogger<AgentRegistryService>? logger = null)
    {
        _agents = agents;
        _selections = selections;
        _events = events;
        _mode = mode;
        _tenantContext = tenantContext;
        _httpContext = httpContext;
        _defaultPersona = defaultPersona?.Value ?? new DefaultPersonaOptions();
        _enablement = enablement;
        _logger = logger;
    }

    /// <inheritdoc />
    public (Guid? TenantId, Guid? UserId) ResolvePrincipal()
        => _mode.Mode == TammaMode.SaaS
            ? (_tenantContext.TenantId, (Guid?)null)
            : ((Guid?)null, CurrentUserId());

    /// <inheritdoc />
    public bool IsEnablementGateActive => _enablement is not null;

    /// <inheritdoc />
    public async Task<Agent?> ResolveUsableAgentAsync(Guid agentId, CancellationToken ct = default)
    {
        var agent = await _agents.GetByIdAsync(agentId, ct);
        if (agent is null)
        {
            return null;
        }
        return await CanUseAsync(agent, ct) ? agent : null;
    }

    /// <inheritdoc />
    public async Task<bool> CanUseAsync(Agent agent, CancellationToken ct = default)
    {
        // Story 32-18 — PUBLIC personas are usable ONLY when enabled for the
        // principal (32-16 read seam); PRIVATE/custom agents are usable iff owned
        // by the principal's scope (implicit enablement by authorship). No code
        // path treats a public persona as usable solely because it is public.
        if (agent.Visibility == AgentVisibility.Public)
        {
            if (_enablement is null)
            {
                // Pre-32-18 fallback for legacy test harnesses that don't wire the
                // enablement seam. Production ALWAYS wires it (DI), so the gate is
                // never bypassed in a real deployment.
                return true;
            }
            return await _enablement.IsEnabledForPrincipalAsync(agent.Id, CurrentPrincipal(), ct);
        }

        return IsOwnedByPrincipal(agent);
    }

    /// <inheritdoc />
    public async Task<Agent?> GetSystemDefaultPublicAsync(string role, CancellationToken ct = default)
    {
        // Story 32-15 — public agents are named cross-role PERSONAS, so the
        // default is the platform-configured DEFAULT PERSONA (by handle, role-
        // independent; the old "public agent whose Role==role" lookup + the
        // >1-per-role ambiguity warning are gone).
        //
        // Story 32-18 — the default must also be ENABLED for the principal. The
        // precedence is:
        //   1. the configured DefaultPersonaName persona, IF the principal enabled it;
        //   2. else the principal's enabled default per 32-16
        //      (GetEnabledDefaultPersonaIdAsync — the configured default if enabled,
        //      else the single enabled persona if unambiguous, else null);
        //   3. else null ⇒ the resolver fails loud (AGENT.RESOLVE.NO_ENABLED_DEFAULT).
        // There is NO empty/plain fallback (feedback_resolution_no_empty_fallback).
        var name = _defaultPersona.DefaultPersonaName;
        var configured = await _agents.GetPublicByNameAsync(name, ct);

        // When the enablement seam is unwired (legacy test harnesses only), keep
        // the pre-32-18 behaviour: the configured default persona MUST be seeded
        // and active, else fail loud.
        if (_enablement is null)
        {
            if (configured is null || configured.Status != AgentStatus.Active)
            {
                _logger?.LogWarning(
                    "agent.default_persona.missing personaName={PersonaName} role={Role} — "
                    + "configured default persona is not seeded/active; failing loud",
                    name, role);
                throw new TammaError(
                    "AGENT_DEFAULT_PERSONA_MISSING",
                    $"Configured default persona '{name}' is not seeded (or not active); "
                    + $"cannot resolve a default for role '{role}'. There is no empty/plain fallback.",
                    new Dictionary<string, object?> { ["personaName"] = name, ["role"] = role },
                    retryable: false,
                    severity: TammaErrorSeverity.High);
            }

            _logger?.LogInformation(
                "agent.default_persona.resolved personaName={PersonaName} agentId={AgentId} role={Role}",
                configured.Name, configured.Id, role);
            return configured;
        }

        var principal = CurrentPrincipal();

        // 1. configured default persona IF enabled for the principal.
        if (configured is not null
            && configured.Status == AgentStatus.Active
            && await _enablement.IsEnabledForPrincipalAsync(configured.Id, principal, ct))
        {
            _logger?.LogInformation(
                "agent.default_persona.resolved branch=configured personaName={PersonaName} agentId={AgentId} role={Role}",
                configured.Name, configured.Id, role);
            return configured;
        }

        // 2. the principal's enabled default per 32-16.
        var enabledDefaultId = await _enablement.GetEnabledDefaultPersonaIdAsync(principal, ct);
        if (enabledDefaultId is Guid id)
        {
            var enabledDefault = await _agents.GetByIdAsync(id, ct);
            if (enabledDefault is { Status: AgentStatus.Active })
            {
                _logger?.LogInformation(
                    "agent.default_persona.resolved branch=enabled-default personaName={PersonaName} agentId={AgentId} role={Role}",
                    enabledDefault.Name, enabledDefault.Id, role);
                return enabledDefault;
            }
        }

        // 3. nothing enabled ⇒ no default. The resolver fails loud
        //    (AGENT.RESOLVE.NO_ENABLED_DEFAULT) — NO empty/plain fallback.
        _logger?.LogWarning(
            "agent.default_persona.none role={Role} configured={PersonaName} — "
            + "principal has enabled no usable persona; resolver will fail loud",
            role, name);
        return null;
    }

    /// <inheritdoc />
    public async Task<AgentRoleSelection> SelectForRoleAsync(
        string role, Guid agentId, Guid? actingUserId, CancellationToken ct = default)
    {
        // Target must be VISIBLE to the principal: a public persona (any) or the
        // principal's own private agent. A cross-scope private target (or a
        // non-existent id) is "not found" — never selectable, never leaked (404).
        var agent = await _agents.GetByIdAsync(agentId, ct);
        if (agent is null || !IsVisibleToPrincipal(agent))
        {
            throw new TammaError(
                "AGENT.SELECT.NOT_FOUND",
                $"Agent '{agentId}' is not selectable for role '{role}' (not public and not owned by the caller).",
                new Dictionary<string, object?> { ["agentId"] = agentId, ["role"] = role },
                retryable: false,
                severity: TammaErrorSeverity.Medium);
        }

        // Story 32-18 — ENABLEMENT GATE. A visible PUBLIC persona that the
        // principal has NOT enabled is a state CONFLICT (it is in the catalog but
        // not exposed), not a missing resource: emit AGENT.SELECT.NOT_ENABLED and
        // throw a 409-mapping TammaError. An own private/custom agent is implicitly
        // enabled by authorship and skips the gate.
        if (agent.Visibility == AgentVisibility.Public && !await CanUseAsync(agent, ct))
        {
            await AppendSelectNotEnabledEventAsync(agent, role, ct);
            _logger?.LogWarning(
                "agent.select.not_enabled agentId={AgentId} personaName={PersonaName} role={Role} — "
                + "public persona is not enabled for the principal; blocking selection",
                agent.Id, agent.Name, role);
            throw new TammaError(
                "AGENT.SELECT.NOT_ENABLED",
                $"Persona '{agent.Name}' is not enabled for this {ModeLabel()}; "
                + $"enable it before selecting it for role '{role}'. There is no empty/plain fallback.",
                new Dictionary<string, object?>
                {
                    ["agentId"] = agentId,
                    ["personaName"] = agent.Name,
                    ["role"] = role,
                },
                retryable: false,
                severity: TammaErrorSeverity.Medium);
        }

        var visibility = ProvenanceFor(agent);
        var (tenantId, userId) = ResolvePrincipal();

        AgentRoleSelection saved;
        bool wasCreated;
        if (_mode.Mode == TammaMode.SaaS)
        {
            if (tenantId is not Guid tid)
            {
                throw new TammaError(
                    "AGENT.SELECT.NO_TENANT",
                    "A role selection in SaaS mode requires tenant context.",
                    severity: TammaErrorSeverity.Medium);
            }
            (saved, wasCreated) = await _selections.UpsertByTenantAsync(
                tid, role, agentId, visibility, actingUserId, ct);
        }
        else
        {
            if (userId is not Guid uid)
            {
                throw new TammaError(
                    "AGENT.SELECT.NO_USER",
                    "A role selection in single-user mode requires a user id.",
                    severity: TammaErrorSeverity.Medium);
            }
            (saved, wasCreated) = await _selections.UpsertByUserAsync(
                uid, role, agentId, visibility, actingUserId, ct);
        }

        _logger?.LogInformation(
            "agent.selected_for_role role={Role} agentId={AgentId} source={Source} created={Created}",
            role, agentId, visibility, wasCreated);

        await AppendSelectionEventAsync(saved, visibility, ct);
        return saved;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, AgentRoleSelection>> GetRoleSelectionsAsync(
        CancellationToken ct = default)
    {
        var (tenantId, userId) = ResolvePrincipal();
        IReadOnlyList<AgentRoleSelection> rows;
        if (_mode.Mode == TammaMode.SaaS)
        {
            rows = tenantId is Guid tid
                ? await _selections.ListByTenantAsync(tid, ct)
                : Array.Empty<AgentRoleSelection>();
        }
        else
        {
            rows = userId is Guid uid
                ? await _selections.ListByUserAsync(uid, ct)
                : Array.Empty<AgentRoleSelection>();
        }

        // One selection per (principal, role) by the unique index — but defend
        // against a duplicate by taking the most recent.
        return rows
            .GroupBy(r => r.Role, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.UpdatedAt).First(),
                StringComparer.Ordinal);
    }

    // ── helpers ──

    private Guid? CurrentUserId()
        => _httpContext.HttpContext?.User?.GetUserId();

    /// <summary>The calling principal as the 32-15/32-16 <see cref="Principal"/>
    /// record (exactly one of TenantId / UserId set per the mode).</summary>
    private Principal CurrentPrincipal()
    {
        var (tenantId, userId) = ResolvePrincipal();
        return new Principal(tenantId, userId);
    }

    private string ModeLabel() => _mode.Mode == TammaMode.SaaS ? "tenant" : "user";

    /// <summary>
    /// True if the agent is OWNED by the calling principal's scope (an own
    /// private/custom agent — implicitly enabled by authorship).
    /// </summary>
    private bool IsOwnedByPrincipal(Agent agent)
    {
        var (tenantId, userId) = ResolvePrincipal();
        return (tenantId is not null && agent.OwnerTenantId == tenantId) ||
               (userId is not null && agent.OwnerUserId == userId);
    }

    /// <summary>
    /// True if the agent is VISIBLE to the calling principal: any public persona,
    /// or a private agent owned by the principal's scope. Visibility is a weaker
    /// check than usability (<see cref="CanUseAsync"/>) — a disabled public
    /// persona is still visible (it is in the catalog), so selecting it is a 409
    /// conflict, not a 404.
    /// </summary>
    private bool IsVisibleToPrincipal(Agent agent)
        => agent.Visibility == AgentVisibility.Public || IsOwnedByPrincipal(agent);

    /// <summary>
    /// Provenance hint stored on a role selection. Must match the source the
    /// resolver (<c>AgentResolverService</c>) stamps for that same selection so
    /// the two read surfaces agree: a principal SELECTING a public agent ⇒
    /// <c>tenant-public</c> (NOT <c>system-public</c> — that label is reserved
    /// for the system-default fallback, which is not a selection); an own-private
    /// agent ⇒ <c>tenant-private</c>.
    /// </summary>
    private string ProvenanceFor(Agent agent)
        => agent.Visibility == AgentVisibility.Public ? "tenant-public" : "tenant-private";

    private async Task AppendSelectionEventAsync(
        AgentRoleSelection sel, string source, CancellationToken ct)
    {
        _ = ct;
        var mode = _mode.Mode == TammaMode.SaaS ? "saas" : "single-user";
        var tags = new Dictionary<string, object?>
        {
            ["agentId"] = sel.AgentId.ToString(),
            ["role"] = sel.Role,
            ["source"] = source,
            ["mode"] = mode,
        };
        if (sel.TenantId is Guid tid) tags["tenantId"] = tid.ToString();
        if (sel.UserId is Guid uid) tags["userId"] = uid.ToString();

        await _events.AppendAsync(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = AgentEventTypes.SelectedForRole,
            // SaaS selection events land in the tenant store (ambient TenantId);
            // single-user selection is a platform-feed row (TenantId null).
            TenantId = sel.TenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = JsonSerializer.Serialize(new
            {
                workflowVersion = WorkflowVersion,
                eventSource = "system",
            }),
            Data = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["role"] = sel.Role,
                ["agentId"] = sel.AgentId.ToString(),
            }),
            CreatedAt = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// Story 32-18 — emit <c>AGENT.SELECT.NOT_ENABLED</c> when a selection of a
    /// public persona is blocked by the enablement gate. Tags
    /// <c>{ agentId, personaName, role, mode, tenantId|userId }</c>.
    /// </summary>
    private async Task AppendSelectNotEnabledEventAsync(Agent agent, string role, CancellationToken ct)
    {
        _ = ct;
        var (tenantId, userId) = ResolvePrincipal();
        var tags = new Dictionary<string, object?>
        {
            ["agentId"] = agent.Id.ToString(),
            ["personaName"] = agent.Name,
            ["role"] = role,
            ["mode"] = _mode.Mode == TammaMode.SaaS ? "saas" : "single-user",
        };
        if (tenantId is Guid tid) tags["tenantId"] = tid.ToString();
        if (userId is Guid uid) tags["userId"] = uid.ToString();

        await _events.AppendAsync(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = AgentEventTypes.SelectNotEnabled,
            // SaaS events land in the tenant store (ambient TenantId); single-user
            // is a platform-feed row (TenantId null).
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = JsonSerializer.Serialize(new
            {
                workflowVersion = WorkflowVersion,
                eventSource = "system",
            }),
            Data = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["role"] = role,
                ["agentId"] = agent.Id.ToString(),
                ["personaName"] = agent.Name,
            }),
            CreatedAt = DateTime.UtcNow,
        });
    }
}
