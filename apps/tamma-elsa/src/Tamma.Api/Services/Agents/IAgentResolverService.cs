namespace Tamma.Api.Services.Agents;

/// <summary>
/// Resolves agent configurations for a given (tenant, role) or
/// (tenant, phase, role) by merging platform defaults with tenant overrides
/// stored in the <c>agent_configs</c> JSONB column.
///
/// Port of <c>RoleBasedAgentResolver</c> from the deleted TS providers
/// package (Story 9-8).
/// </summary>
public interface IAgentResolverService
{
    /// <summary>
    /// Resolve the agent configuration for a (tenant, role) pair.
    ///
    /// Resolution order:
    /// <list type="number">
    ///   <item>Load tenant override (if <paramref name="tenantId"/> is set).</item>
    ///   <item>Merge on top of platform default for the role.</item>
    ///   <item>Validate all required fields are present and non-empty.</item>
    /// </list>
    /// </summary>
    /// <param name="tenantId">The tenant scope; <c>null</c> returns platform default.</param>
    /// <param name="role">One of the 8 valid roles (see <see cref="RolePhaseMap.ValidRoles"/>).</param>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="role"/> is unknown or forbidden.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if a required field is missing after merge.
    /// </exception>
    Task<ResolvedAgentConfig> ResolveAsync(Guid? tenantId, string role);

    /// <summary>
    /// Resolve the agent configuration for a specific (tenant, phase, role).
    ///
    /// Validates that <paramref name="role"/> is eligible for
    /// <paramref name="phase"/> via <see cref="RolePhaseMap.IsRoleEligibleForPhase"/>,
    /// then delegates to <see cref="ResolveAsync"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="phase"/> is unknown or
    /// <paramref name="role"/> is not eligible for the phase.
    /// </exception>
    Task<ResolvedAgentConfig> ResolveForPhaseAsync(Guid? tenantId, string phase, string role);

    /// <summary>
    /// Resolve for a specific (tenant, phase, role) plus per-task overrides.
    /// Overrides are clamped (finding 007): budget is <c>Math.Min</c>'d against
    /// the role's ceiling, tool lists are intersected, and
    /// <c>bypassPermissions</c> requires <c>TAMMA_ALLOW_BYPASS_PERMISSIONS=true</c>
    /// from configuration — without that gate the role's permission mode is
    /// preserved.
    /// </summary>
    Task<ResolvedAgentConfig> ResolveForPhaseAsync(
        Guid? tenantId, string phase, string role,
        Dtos.Agents.TaskOverrides? overrides);

    /// <summary>
    /// Story 32-2 — resolve the EFFECTIVE first-class agent for the calling
    /// principal + <paramref name="role"/> via the deterministic precedence
    /// chain (private selection → public selection → system-default public →
    /// FAIL LOUD). Returns an enriched <see cref="ResolvedAgentConfig"/> carrying
    /// <see cref="ResolvedAgentConfig.AgentId"/>,
    /// <see cref="ResolvedAgentConfig.AgentVersion"/> and an extended
    /// <see cref="ResolvedAgentConfig.Source"/> (<c>tenant-private</c> /
    /// <c>tenant-public</c> / <c>system-public</c>).
    ///
    /// <para><b>Never returns a blank/empty config.</b> The fourth branch emits
    /// <c>AGENT.RESOLVE.FAILED</c>, best-effort records a <c>MISSING_CONFIG</c>
    /// gap, then throws <see cref="Tamma.Core.TammaError"/>
    /// <c>AGENT.RESOLVE.NO_DEFAULT</c> (severity High) — mirroring the
    /// prompt/convention fail-loud rule.</para>
    ///
    /// <para>Story 32-18 — the optional <paramref name="action"/> is the Epic-27
    /// action key threaded through to the persona prompt source
    /// (<c>IPersonaPromptResolver</c>) and the custom-agent prompt source
    /// (<c>ICustomAgentPromptResolver</c>), so the persona prompt resolves at
    /// <c>(principal, role, action)</c> — matching the <c>LlmCallRequest.action</c>
    /// the call-LLM endpoint (32-5) passes. When <c>null</c>, the role-system
    /// (identity preamble) / action-default branch is used (still tenant→system→
    /// error, never empty).</para>
    /// </summary>
    /// <exception cref="ArgumentException">Unknown role.</exception>
    /// <exception cref="Tamma.Core.TammaError">No agent resolvable
    /// (<c>AGENT.RESOLVE.NO_DEFAULT</c> or, when the tenant has enabled nothing,
    /// <c>AGENT.RESOLVE.NO_ENABLED_DEFAULT</c>).</exception>
    Task<ResolvedAgentConfig> ResolveForRoleAsync(
        string role, string? action = null, CancellationToken ct = default);

    /// <summary>
    /// Story 32-2 — same precedence chain as <see cref="ResolveForRoleAsync"/>
    /// plus phase-eligibility validation
    /// (<see cref="RolePhaseMap.IsRoleEligibleForPhase"/>). Unknown phase or an
    /// ineligible (phase, role) pair throws <see cref="ArgumentException"/>
    /// before any resolution attempt.
    /// </summary>
    Task<ResolvedAgentConfig> ResolveForRoleAndPhaseAsync(
        string phase, string role, CancellationToken ct = default);
}
