namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-16 — the WRITE/ADMIN enablement service. Extends the read seam
/// (<see cref="ITenantAgentEnablementReader"/>) with the enable/disable/list
/// surface that backs <c>PUT/DELETE/GET /api/agents/.../enablement</c>. ONE
/// implementation (<see cref="TenantAgentEnablementService"/>) implements both
/// interfaces; 32-18 depends only on the reader and never sees these methods.
///
/// <para>The write methods derive the principal from the ambient request
/// (<c>ITammaModeProvider</c> + <c>ITenantContext</c>/<c>ClaimsPrincipal</c>):
/// SaaS ⇒ <c>TenantId</c>; single-user ⇒ <c>UserId</c>. They validate the target
/// is in (public ∪ own-private) and emit exactly one DCB event per successful
/// write.</para>
/// </summary>
public interface ITenantAgentEnablementService : ITenantAgentEnablementReader
{
    /// <summary>
    /// Enable a public persona for the current principal: idempotent upsert
    /// setting <c>Enabled = true</c>; emits <c>AGENT.ENABLED.SUCCESS</c>.
    /// Validates the target ∈ (public ∪ own-private) — a cross-scope/non-existent
    /// target throws <see cref="Tamma.Core.TammaError"/>
    /// <c>AGENT.ENABLEMENT.NOT_FOUND</c> (mapped to 404, existence-leak-safe).
    /// Enabling an own private/custom agent is a no-op confirm (implicitly
    /// enabled) with no row and no event.
    /// </summary>
    Task<AgentEnablementState> EnableAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>
    /// Disable a public persona for the current principal: sets the row's
    /// <c>Enabled = false</c> (default-deny removes it from the usable set);
    /// emits <c>AGENT.DISABLED.SUCCESS</c>. Disabling an OWN private/custom agent
    /// throws <c>AGENT.ENABLEMENT.PRIVATE_NOT_DISABLEABLE</c> (mapped to 409) — it
    /// is implicitly enabled by authorship and is removed via archive (32-2), not
    /// here. A cross-scope/non-existent target ⇒ 404.
    /// </summary>
    Task<AgentEnablementState> DisableAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>
    /// Catalog view: every visible public persona with its enabled flag, plus the
    /// principal's own-private agents (marked implicitly enabled). Any member may
    /// read (reads are not gated).
    /// </summary>
    Task<IReadOnlyList<AgentEnablementState>> ListAsync(CancellationToken ct = default);
}

/// <summary>
/// Story 32-16 — one row of the enablement catalog view. <see cref="PersonaName"/>
/// is the public persona handle (or the private agent's name);
/// <see cref="ImplicitlyEnabled"/> is true for own-private agents (which cannot be
/// toggled via the enablement API).
/// </summary>
public sealed record AgentEnablementState(
    Guid AgentId,
    string? PersonaName,
    bool Enabled,
    bool ImplicitlyEnabled);
