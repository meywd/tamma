using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<AgentRegistryService>? _logger;

    public AgentRegistryService(
        IAgentRepository agents,
        IAgentSelectionRepository selections,
        IEventRepository events,
        ITammaModeProvider mode,
        ITenantContext tenantContext,
        IHttpContextAccessor httpContext,
        ILogger<AgentRegistryService>? logger = null)
    {
        _agents = agents;
        _selections = selections;
        _events = events;
        _mode = mode;
        _tenantContext = tenantContext;
        _httpContext = httpContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public (Guid? TenantId, Guid? UserId) ResolvePrincipal()
        => _mode.Mode == TammaMode.SaaS
            ? (_tenantContext.TenantId, (Guid?)null)
            : ((Guid?)null, CurrentUserId());

    /// <inheritdoc />
    public async Task<Agent?> ResolveUsableAgentAsync(Guid agentId, CancellationToken ct = default)
    {
        var agent = await _agents.GetByIdAsync(agentId, ct);
        if (agent is null)
        {
            return null;
        }
        return CanUse(agent) ? agent : null;
    }

    /// <inheritdoc />
    public async Task<Agent?> GetSystemDefaultPublicAsync(string role, CancellationToken ct = default)
    {
        // The system default for a role is the shipped public agent whose Role
        // matches (one per role from AgentEntitySeeder, handle "tamma-<role>").
        // Public agents are visible to every principal; pass null scope.
        var visible = await _agents.ListVisibleAsync(tenantId: null, userId: null, ct);
        return visible.FirstOrDefault(a =>
            a.Visibility == AgentVisibility.Public &&
            a.Status == AgentStatus.Active &&
            string.Equals(a.Role, role, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public async Task<AgentRoleSelection> SelectForRoleAsync(
        string role, Guid agentId, Guid? actingUserId, CancellationToken ct = default)
    {
        // Target must be in (public ∪ own private). A cross-scope private target
        // (or a non-existent id) is "not found" — never selectable, never leaked.
        var agent = await ResolveUsableAgentAsync(agentId, ct);
        if (agent is null)
        {
            throw new TammaError(
                "AGENT.SELECT.NOT_FOUND",
                $"Agent '{agentId}' is not selectable for role '{role}' (not public and not owned by the caller).",
                new Dictionary<string, object?> { ["agentId"] = agentId, ["role"] = role },
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

    /// <summary>
    /// True if the calling principal may USE the agent: any public agent, or a
    /// private agent owned by the caller's scope.
    /// </summary>
    private bool CanUse(Agent agent)
    {
        if (agent.Visibility == AgentVisibility.Public)
        {
            return true;
        }
        var (tenantId, userId) = ResolvePrincipal();
        return (tenantId is not null && agent.OwnerTenantId == tenantId) ||
               (userId is not null && agent.OwnerUserId == userId);
    }

    /// <summary>
    /// Recompute provenance at resolve/select time — never trust a stored hint.
    /// A public agent ⇒ <c>system-public</c>; an own-private agent ⇒
    /// <c>tenant-private</c>. (<c>tenant-public</c> is the wire term for a
    /// principal SELECTING a public agent; the resolver stamps that variant.)
    /// </summary>
    private string ProvenanceFor(Agent agent)
        => agent.Visibility == AgentVisibility.Public ? "system-public" : "tenant-private";

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
}
