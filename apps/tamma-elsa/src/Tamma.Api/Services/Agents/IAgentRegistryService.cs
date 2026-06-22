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
    /// Story 32-18 — true when the per-tenant ENABLEMENT GATE (32-16 read seam) is
    /// wired (production ALWAYS wires it via DI). The resolver consumes this to pick
    /// the accurate fail-loud code: when the gate is active, a fail-loud after a
    /// non-null-but-unmaterialisable enabled default is <c>AGENT.RESOLVE.NO_ENABLED_DEFAULT</c>
    /// (the principal HAD an enabled default); when the gate is unwired (legacy
    /// seam-less test harnesses), the canonical <c>AGENT.RESOLVE.NO_DEFAULT</c> stays.
    /// </summary>
    bool IsEnablementGateActive { get; }

    /// <summary>
    /// Resolve an agent the caller may USE: an ENABLED public persona (32-18 gate
    /// over 32-16), or a private agent owned by the caller's scope (implicitly
    /// enabled). Returns <c>null</c> for a non-existent id, a cross-scope private
    /// id (caller can't see it), OR a public persona NOT enabled for the
    /// principal — the endpoint maps null → 404 to avoid an existence leak.
    /// </summary>
    Task<Agent?> ResolveUsableAgentAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>
    /// Story 32-18 — the usability predicate, enablement-aware: a PUBLIC persona
    /// is usable iff it is ENABLED for the principal (32-16 read seam); a private
    /// agent is usable iff owned by the principal's scope (implicit enablement by
    /// authorship). No code path treats a public persona as usable solely because
    /// it is public. The resolve precedence calls this to decide whether a stored
    /// selection still resolves or must degrade to the enabled default.
    /// </summary>
    Task<bool> CanUseAsync(Agent agent, CancellationToken ct = default);

    /// <summary>
    /// The tenant's ENABLED default persona (Story 32-18 over 32-15/32-16).
    /// Personas are cross-role (<see cref="Agent.Role"/> is NULL), so this no
    /// longer matches <c>Role == role</c>: it returns the configured
    /// <c>DefaultPersonaName</c> persona IF the principal has enabled it, else the
    /// principal's enabled default per 32-16
    /// (<c>GetEnabledDefaultPersonaIdAsync</c>), else <c>null</c>. A <c>null</c>
    /// result is the resolver's fail-loud signal
    /// (<c>AGENT.RESOLVE.NO_ENABLED_DEFAULT</c>) — never an empty/plain fallback.
    /// </summary>
    Task<Agent?> GetSystemDefaultPublicAsync(string role, CancellationToken ct = default);

    /// <summary>
    /// Persist which agent serves <paramref name="role"/> for the calling
    /// principal. Validates the target is selectable:
    /// <list type="bullet">
    ///   <item>a cross-scope/non-existent target throws
    ///     <see cref="Tamma.Core.TammaError"/> <c>AGENT.SELECT.NOT_FOUND</c>
    ///     (mapped to 404 — existence-leak-safe);</item>
    ///   <item>Story 32-18 — a PUBLIC persona NOT enabled for the principal throws
    ///     <c>AGENT.SELECT.NOT_ENABLED</c> (mapped to 409
    ///     <c>agent_not_enabled</c>) and emits the
    ///     <see cref="AgentEventTypes.SelectNotEnabled"/> audit event — selecting a
    ///     disabled persona is a state conflict, not a missing resource;</item>
    ///   <item>an own private/custom agent is implicitly enabled and is accepted.</item>
    /// </list>
    /// Emits <c>AGENT.SELECTED_FOR_ROLE.SUCCESS</c> on a real upsert.
    /// </summary>
    Task<AgentRoleSelection> SelectForRoleAsync(
        string role, Guid agentId, Guid? actingUserId, CancellationToken ct = default);

    /// <summary>The principal's current role→selection map (role ⇒ selection).</summary>
    Task<IReadOnlyDictionary<string, AgentRoleSelection>> GetRoleSelectionsAsync(
        CancellationToken ct = default);
}
