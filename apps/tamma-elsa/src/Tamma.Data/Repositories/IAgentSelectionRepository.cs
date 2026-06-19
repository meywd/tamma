using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 32-2 — persistence port for <see cref="AgentRoleSelection"/> (which
/// agent serves a role for a principal). Dual-scoping mirrors
/// <see cref="IPromptRepository"/>:
/// <list type="bullet">
///   <item>single-user ⇒ rows keyed by <c>user_id</c> (<c>tenant_id</c> NULL),
///     persisted on the control plane.</item>
///   <item>SaaS ⇒ rows keyed by <c>tenant_id</c> (<c>user_id</c> NULL),
///     persisted in the tenant's <c>t_&lt;hex&gt;</c> schema.</item>
/// </list>
/// The methods are parallel — the caller picks the right one based on mode; no
/// method silently joins both planes. The <c>principal_xor</c> CHECK +
/// <c>UNIQUE NULLS NOT DISTINCT (tenant_id, user_id, role)</c> index keep the
/// two row-spaces disjoint and at most one row per <c>(principal, role)</c>.
/// </summary>
public interface IAgentSelectionRepository
{
    // ── single-user mode (user-keyed; control-plane resident) ──

    Task<AgentRoleSelection?> GetByUserAsync(
        Guid userId, string role, CancellationToken ct = default);

    Task<IReadOnlyList<AgentRoleSelection>> ListByUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Insert or update the user's selection for a role. Returns the
    /// persisted row + whether it was created (vs updated).</summary>
    Task<(AgentRoleSelection Entity, bool WasCreated)> UpsertByUserAsync(
        Guid userId, string role, Guid agentId, string visibility,
        Guid? updatedBy, CancellationToken ct = default);

    // ── SaaS mode (tenant-keyed; tenant-schema resident) ──

    Task<AgentRoleSelection?> GetByTenantAsync(
        Guid tenantId, string role, CancellationToken ct = default);

    Task<IReadOnlyList<AgentRoleSelection>> ListByTenantAsync(
        Guid tenantId, CancellationToken ct = default);

    Task<(AgentRoleSelection Entity, bool WasCreated)> UpsertByTenantAsync(
        Guid tenantId, string role, Guid agentId, string visibility,
        Guid? updatedBy, CancellationToken ct = default);
}
