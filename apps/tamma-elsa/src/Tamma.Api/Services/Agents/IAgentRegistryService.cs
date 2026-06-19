using Tamma.Data.Entities;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-2 — the entity-aware agent registry seam over the Story 32-1
/// <see cref="Data.Repositories.IAgentRepository"/>. Owns:
/// <list type="bullet">
///   <item>resolving the calling principal's scope from the process mode
///     (SaaS ⇒ tenant id; single-user ⇒ user id);</item>
///   <item>visibility-scoped membership checks — is a target agent in
///     (public ∪ own private)? — for selection validation;</item>
///   <item>per-principal role→agent selection persistence (emits
///     <c>AGENT.SELECTED_FOR_ROLE.SUCCESS</c>);</item>
///   <item>resolving the SYSTEM-DEFAULT public agent for a role (the shipped
///     <c>tamma-&lt;role&gt;</c> public agent — Story 32-1 seeder).</item>
/// </list>
/// CRUD (create/version/archive/get/list) flows through the existing
/// <see cref="Data.Repositories.IAgentRepository"/>; this service is the
/// resolution/selection layer 32-2 adds. Provider credentials are NEVER
/// resolved here (that is Story 32-3) — agent configs stay credential-agnostic.
/// </summary>
public interface IAgentRegistryService
{
    /// <summary>The calling principal derived from the process mode: SaaS ⇒
    /// (tenantId, null); single-user ⇒ (null, userId).</summary>
    (Guid? TenantId, Guid? UserId) ResolvePrincipal();

    /// <summary>
    /// Resolve an agent the caller may USE: a public agent (any), or a private
    /// agent owned by the caller's scope. Returns <c>null</c> for a
    /// non-existent id OR a cross-scope private id (caller can't see it) — the
    /// endpoint maps null → 404 to avoid an existence leak.
    /// </summary>
    Task<Agent?> ResolveUsableAgentAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>
    /// The system-default public agent for a role: the active, public agent
    /// whose <see cref="Agent.Role"/> matches (the <c>tamma-&lt;role&gt;</c>
    /// shipped seed). Returns <c>null</c> when none is seeded — the resolver's
    /// fail-loud branch (AC 9) then fires.
    /// </summary>
    Task<Agent?> GetSystemDefaultPublicAsync(string role, CancellationToken ct = default);

    /// <summary>
    /// Persist which agent serves <paramref name="role"/> for the calling
    /// principal. Validates the target is in (public ∪ own private); a
    /// cross-scope/non-existent target throws
    /// <see cref="Tamma.Core.TammaError"/> <c>AGENT.SELECT.NOT_FOUND</c> (mapped
    /// to 404). Emits <c>AGENT.SELECTED_FOR_ROLE.SUCCESS</c> on a real upsert.
    /// </summary>
    Task<AgentRoleSelection> SelectForRoleAsync(
        string role, Guid agentId, Guid? actingUserId, CancellationToken ct = default);

    /// <summary>The principal's current role→selection map (role ⇒ selection).</summary>
    Task<IReadOnlyDictionary<string, AgentRoleSelection>> GetRoleSelectionsAsync(
        CancellationToken ct = default);
}
