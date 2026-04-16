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
}
