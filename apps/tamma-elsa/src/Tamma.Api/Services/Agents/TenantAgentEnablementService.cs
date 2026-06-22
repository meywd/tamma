using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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
/// Story 32-16 — default <see cref="ITenantAgentEnablementService"/> (and
/// therefore <see cref="ITenantAgentEnablementReader"/>). Owns per-tenant
/// agent/persona ENABLEMENT (catalog membership). Reads/writes the CP-resident
/// <c>tenant_agent_enablements</c> table directly (CP-resident in BOTH modes),
/// validates targets against the public <see cref="Agent"/> catalog (∪ the
/// principal's own-private agents), and emits the DCB enablement events.
///
/// <para>The process mode (<see cref="ITammaModeProvider"/>) settles the
/// principal: SaaS ⇒ ambient tenant id; single-user ⇒ the calling user id —
/// exactly one of <c>(TenantId, UserId)</c> per the entity XOR.</para>
///
/// <para><b>Boundary (32-18).</b> This service ships the reader PRIMITIVES; it
/// does NOT wire the gate into the registry/resolver. <c>CanUse</c> →
/// <c>IsEnabledForPrincipal</c> and the enablement-aware
/// <c>SelectForRoleAsync</c>/<c>ResolveUsableAgentAsync</c>/<c>ListVisibleAsync</c>/
/// <c>GetSystemDefaultPublicAsync</c> are sibling story 32-18, which injects
/// <see cref="ITenantAgentEnablementReader"/>.</para>
/// </summary>
public sealed class TenantAgentEnablementService : ITenantAgentEnablementService
{
    private const string WorkflowVersion = "1.0.0";

    private readonly ControlPlaneDbContext _db;
    private readonly IAgentRepository _agents;
    private readonly IEventRepository _events;
    private readonly ITammaModeProvider _mode;
    private readonly ITenantContext _tenantContext;
    private readonly IHttpContextAccessor _httpContext;
    private readonly DefaultPersonaOptions _defaultPersona;
    private readonly ILogger<TenantAgentEnablementService>? _logger;

    public TenantAgentEnablementService(
        ControlPlaneDbContext db,
        IAgentRepository agents,
        IEventRepository events,
        ITammaModeProvider mode,
        ITenantContext tenantContext,
        IHttpContextAccessor httpContext,
        IOptions<DefaultPersonaOptions>? defaultPersona = null,
        ILogger<TenantAgentEnablementService>? logger = null)
    {
        _db = db;
        _agents = agents;
        _events = events;
        _mode = mode;
        _tenantContext = tenantContext;
        _httpContext = httpContext;
        _defaultPersona = defaultPersona?.Value ?? new DefaultPersonaOptions();
        _logger = logger;
    }

    // ── write/admin surface ──

    /// <inheritdoc />
    public async Task<AgentEnablementState> EnableAsync(Guid agentId, CancellationToken ct = default)
    {
        var principal = ResolvePrincipal();
        var agent = await ResolveVisibleAsync(agentId, principal, ct);
        if (agent is null)
        {
            throw NotFound(agentId);
        }

        // Own private/custom agent ⇒ implicitly enabled by authorship. Enabling is
        // a no-op confirm — no row, no event.
        if (agent.Visibility == AgentVisibility.Private)
        {
            _logger?.LogInformation(
                "agent.enablement.enable_private_noop agentId={AgentId} mode={Mode} — "
                + "own private/custom agents are implicitly enabled",
                agentId, ModeWire());
            return new AgentEnablementState(agent.Id, agent.Name, Enabled: true, ImplicitlyEnabled: true);
        }

        var (created, row) = await UpsertEnabledAsync(principal, agentId, enabled: true, ct);

        _logger?.LogInformation(
            "agent.enablement.enabled agentId={AgentId} personaName={PersonaName} mode={Mode} created={Created}",
            agentId, agent.Name, ModeWire(), created);

        await AppendEventAsync(AgentEnablementEventTypes.Enabled, agent, principal, ct);
        return new AgentEnablementState(agent.Id, agent.Name, Enabled: row.Enabled, ImplicitlyEnabled: false);
    }

    /// <inheritdoc />
    public async Task<AgentEnablementState> DisableAsync(Guid agentId, CancellationToken ct = default)
    {
        var principal = ResolvePrincipal();
        var agent = await ResolveVisibleAsync(agentId, principal, ct);
        if (agent is null)
        {
            throw NotFound(agentId);
        }

        // Disabling an own private/custom agent is rejected (409). Implicitly
        // enabled by authorship; remove via archive (32-2), not here.
        if (agent.Visibility == AgentVisibility.Private)
        {
            _logger?.LogWarning(
                "agent.enablement.disable_private_rejected agentId={AgentId} mode={Mode} — "
                + "own private/custom agents are implicitly enabled (remove via archive, 32-2)",
                agentId, ModeWire());
            throw new TammaError(
                "AGENT.ENABLEMENT.PRIVATE_NOT_DISABLEABLE",
                $"Agent '{agentId}' is an own private/custom agent — implicitly enabled by "
                + "authorship and cannot be disabled via the enablement API (archive it instead).",
                new Dictionary<string, object?> { ["agentId"] = agentId },
                retryable: false,
                severity: TammaErrorSeverity.Medium);
        }

        var (_, row) = await UpsertEnabledAsync(principal, agentId, enabled: false, ct);

        _logger?.LogInformation(
            "agent.enablement.disabled agentId={AgentId} personaName={PersonaName} mode={Mode}",
            agentId, agent.Name, ModeWire());

        await AppendEventAsync(AgentEnablementEventTypes.Disabled, agent, principal, ct);
        return new AgentEnablementState(agent.Id, agent.Name, Enabled: row.Enabled, ImplicitlyEnabled: false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentEnablementState>> ListAsync(CancellationToken ct = default)
    {
        var principal = ResolvePrincipal();
        var visible = await _agents.ListVisibleAsync(principal.TenantId, principal.UserId, ct);
        var live = visible.Where(a => a.Status == AgentStatus.Active).ToList();

        var enabledPublicIds = await EnabledPublicRowIdsAsync(principal, ct);

        var states = live
            .Select(a => a.Visibility == AgentVisibility.Public
                ? new AgentEnablementState(a.Id, a.Name, enabledPublicIds.Contains(a.Id), ImplicitlyEnabled: false)
                : new AgentEnablementState(a.Id, a.Name, Enabled: true, ImplicitlyEnabled: true))
            .OrderBy(s => s.PersonaName, StringComparer.Ordinal)
            .ToList();

        _logger?.LogInformation(
            "agent.enablement.list_requested mode={Mode} enabled={Enabled} disabled={Disabled}",
            ModeWire(),
            states.Count(s => s.Enabled),
            states.Count(s => !s.Enabled));

        return states;
    }

    // ── read seam (consumed by 32-18) ──

    /// <inheritdoc />
    public async Task<bool> IsEnabledForPrincipalAsync(
        Guid agentId, Principal principal, CancellationToken ct = default)
    {
        var agent = await _agents.GetByIdAsync(agentId, ct);
        if (agent is null || agent.Status != AgentStatus.Active)
        {
            _logger?.LogDebug("agent.enablement.is_enabled branch=absent_or_retired agentId={AgentId}", agentId);
            return false;
        }

        // Own private/custom agent ⇒ implicitly enabled (no row required).
        if (agent.Visibility == AgentVisibility.Private)
        {
            var owns = (principal.TenantId is Guid tid && agent.OwnerTenantId == tid)
                       || (principal.UserId is Guid uid && agent.OwnerUserId == uid);
            _logger?.LogDebug(
                "agent.enablement.is_enabled branch=implicit_private agentId={AgentId} owns={Owns}",
                agentId, owns);
            return owns;
        }

        // Public persona ⇒ enabled iff a row with Enabled=true exists.
        var enabled = await EnabledRowExistsAsync(principal, agentId, ct);
        _logger?.LogDebug(
            "agent.enablement.is_enabled branch=public agentId={AgentId} enabled={Enabled}",
            agentId, enabled);
        return enabled;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListEnabledPublicAgentIdsAsync(
        Principal principal, CancellationToken ct = default)
    {
        var ids = await EnabledPublicRowIdsAsync(principal, ct);
        return ids.ToList();
    }

    /// <inheritdoc />
    public async Task<Guid?> GetEnabledDefaultPersonaIdAsync(
        Principal principal, CancellationToken ct = default)
    {
        var enabledIds = await EnabledPublicRowIdsAsync(principal, ct);
        if (enabledIds.Count == 0)
        {
            _logger?.LogDebug("agent.enablement.default_persona outcome=none mode={Mode}", ModeWire());
            return null;
        }

        // Configured default persona, if it is one of the enabled set.
        var configured = await _agents.GetPublicByNameAsync(_defaultPersona.DefaultPersonaName, ct);
        if (configured is not null
            && configured.Status == AgentStatus.Active
            && enabledIds.Contains(configured.Id))
        {
            _logger?.LogDebug(
                "agent.enablement.default_persona outcome=configured personaName={PersonaName} agentId={AgentId}",
                configured.Name, configured.Id);
            return configured.Id;
        }

        // Else the single enabled persona, if unambiguous.
        if (enabledIds.Count == 1)
        {
            var only = enabledIds.Single();
            _logger?.LogDebug("agent.enablement.default_persona outcome=single agentId={AgentId}", only);
            return only;
        }

        _logger?.LogDebug(
            "agent.enablement.default_persona outcome=ambiguous count={Count} mode={Mode}",
            enabledIds.Count, ModeWire());
        return null;
    }

    // ── helpers ──

    /// <summary>The calling principal derived from the process mode.</summary>
    private Principal ResolvePrincipal()
        => _mode.Mode == TammaMode.SaaS
            ? Principal.ForTenant(_tenantContext.TenantId)
            : Principal.ForUser(_httpContext.HttpContext?.User?.GetUserId());

    private Guid? CurrentUserId() => _httpContext.HttpContext?.User?.GetUserId();

    private string ModeWire() => _mode.Mode == TammaMode.SaaS ? "saas" : "single-user";

    /// <summary>
    /// Resolve an agent the principal may SEE for an enablement write: any public
    /// agent (active), or a private agent owned by the principal's scope. Returns
    /// null for a non-existent id, an archived id, OR a cross-scope private id
    /// (no existence leak).
    /// </summary>
    private async Task<Agent?> ResolveVisibleAsync(Guid agentId, Principal principal, CancellationToken ct)
    {
        var agent = await _agents.GetByIdAsync(agentId, ct);
        if (agent is null || agent.Status != AgentStatus.Active)
        {
            return null;
        }
        if (agent.Visibility == AgentVisibility.Public)
        {
            return agent;
        }
        var owns = (principal.TenantId is Guid tid && agent.OwnerTenantId == tid)
                   || (principal.UserId is Guid uid && agent.OwnerUserId == uid);
        return owns ? agent : null;
    }

    private static TammaError NotFound(Guid agentId)
        => new(
            "AGENT.ENABLEMENT.NOT_FOUND",
            $"Agent '{agentId}' is not enableable (not public and not owned by the caller).",
            new Dictionary<string, object?> { ["agentId"] = agentId },
            retryable: false,
            severity: TammaErrorSeverity.Medium);

    /// <summary>
    /// Upsert the principal's enablement row for an agent to the given
    /// <paramref name="enabled"/> flag. Idempotent: re-running with the same flag
    /// touches the same single row (no duplicate). Returns whether a row was
    /// created plus the persisted row.
    /// </summary>
    private async Task<(bool Created, TenantAgentEnablement Row)> UpsertEnabledAsync(
        Principal principal, Guid agentId, bool enabled, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var actingUser = CurrentUserId();

        var existing = await FindRowAsync(principal, agentId, ct);
        if (existing is not null)
        {
            existing.Enabled = enabled;
            existing.UpdatedAt = now;
            existing.UpdatedBy = actingUser;
            await _db.SaveChangesAsync(ct);
            return (false, existing);
        }

        var row = new TenantAgentEnablement
        {
            Id = Guid.NewGuid(),
            TenantId = principal.TenantId,
            UserId = principal.UserId,
            AgentId = agentId,
            Enabled = enabled,
            CreatedAt = now,
            CreatedBy = actingUser,
            UpdatedAt = now,
            UpdatedBy = actingUser,
        };
        _db.TenantAgentEnablements.Add(row);
        await _db.SaveChangesAsync(ct);
        return (true, row);
    }

    private Task<TenantAgentEnablement?> FindRowAsync(Principal principal, Guid agentId, CancellationToken ct)
        => _db.TenantAgentEnablements.FirstOrDefaultAsync(
            r => r.TenantId == principal.TenantId
                 && r.UserId == principal.UserId
                 && r.AgentId == agentId,
            ct);

    private async Task<bool> EnabledRowExistsAsync(Principal principal, Guid agentId, CancellationToken ct)
        => await _db.TenantAgentEnablements.AnyAsync(
            r => r.TenantId == principal.TenantId
                 && r.UserId == principal.UserId
                 && r.AgentId == agentId
                 && r.Enabled,
            ct);

    /// <summary>
    /// The principal's enabled public agent ids, intersected with the LIVE public
    /// catalog so a row pointing at a retired/deleted persona resolves out.
    /// </summary>
    private async Task<HashSet<Guid>> EnabledPublicRowIdsAsync(Principal principal, CancellationToken ct)
    {
        var enabledRowIds = await _db.TenantAgentEnablements
            .Where(r => r.TenantId == principal.TenantId
                        && r.UserId == principal.UserId
                        && r.Enabled)
            .Select(r => r.AgentId)
            .ToListAsync(ct);

        if (enabledRowIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var rowSet = enabledRowIds.ToHashSet();

        // Keep only ids that are still a live (active, public) agent.
        var livePublic = await _db.Agents
            .Where(a => a.Visibility == AgentVisibility.Public
                        && a.Status == AgentStatus.Active
                        && rowSet.Contains(a.Id))
            .Select(a => a.Id)
            .ToListAsync(ct);

        return livePublic.ToHashSet();
    }

    private async Task AppendEventAsync(
        string eventType, Agent agent, Principal principal, CancellationToken ct)
    {
        _ = ct;
        var tags = new Dictionary<string, object?>
        {
            ["agentId"] = agent.Id.ToString(),
            ["personaName"] = agent.Name,
            ["mode"] = ModeWire(),
        };
        if (principal.TenantId is Guid tid) tags["tenantId"] = tid.ToString();
        if (principal.UserId is Guid uid) tags["userId"] = uid.ToString();

        await _events.AppendAsync(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = eventType,
            // SaaS enablement events land in the tenant store (ambient TenantId);
            // single-user enablement is a platform-feed row (TenantId null).
            TenantId = principal.TenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = JsonSerializer.Serialize(new
            {
                workflowVersion = WorkflowVersion,
                eventSource = "system",
            }),
            Data = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["agentId"] = agent.Id.ToString(),
                ["personaName"] = agent.Name,
            }),
            CreatedAt = DateTime.UtcNow,
        });
    }
}
