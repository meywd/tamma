namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-16 — the READ-ONLY enablement seam (ISP split). This is the seam the
/// sibling story <b>32-18</b> injects and consumes to GATE selection/resolution
/// and to resolve the enabled default — it never touches the write methods on
/// <see cref="ITenantAgentEnablementService"/>.
///
/// <para>Enablement = <i>catalog membership</i> (which PUBLIC personas a principal
/// exposes). Own private/custom agents are implicitly enabled (no row required).
/// A public persona is enabled iff an enablement row with <c>Enabled = true</c>
/// exists — default-deny otherwise (the seeded-default carve-out keeps a fresh
/// tenant usable; the resolve-time fail-loud lives in 32-18).</para>
///
/// <para>All methods are async and take an explicit <see cref="Principal"/> so the
/// consumer (32-18) passes its already-resolved principal; the write methods on
/// the extending service derive the principal from the ambient request.</para>
/// </summary>
public interface ITenantAgentEnablementReader
{
    /// <summary>
    /// True iff the agent is part of the principal's usable catalog: an own
    /// private/custom agent ⇒ implicitly <c>true</c> (no row required); a public
    /// persona ⇒ an enablement row with <c>Enabled = true</c> exists. An absent or
    /// disabled row for a public persona ⇒ <c>false</c> (default-deny). A target
    /// that is neither public nor the principal's own private ⇒ <c>false</c>. This
    /// is the gate 32-18 calls from <c>CanUse</c> / <c>SelectForRoleAsync</c> /
    /// <c>ResolveUsableAgentAsync</c>.
    /// </summary>
    Task<bool> IsEnabledForPrincipalAsync(Guid agentId, Principal principal, CancellationToken ct = default);

    /// <summary>
    /// The set of PUBLIC agent ids enabled for the principal — for 32-18's
    /// <c>ListVisibleAsync</c> (<c>enabled(public) ∪ own-private</c>). Excludes
    /// disabled rows, no-row publics, private ids, and any persona that is no
    /// longer a live (active, public) agent.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListEnabledPublicAgentIdsAsync(Principal principal, CancellationToken ct = default);

    /// <summary>
    /// The principal's enabled DEFAULT persona id: the configured
    /// <c>DefaultPersonaName</c> (Story 32-15) if it is enabled, else the single
    /// enabled persona if unambiguous, else <c>null</c>. 32-18 CONSUMES this for
    /// <c>GetSystemDefaultPublicAsync</c> — it never redefines it.
    /// </summary>
    Task<Guid?> GetEnabledDefaultPersonaIdAsync(Principal principal, CancellationToken ct = default);
}
