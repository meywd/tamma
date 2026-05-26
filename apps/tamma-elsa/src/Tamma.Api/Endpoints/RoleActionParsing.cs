using Microsoft.AspNetCore.Http;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Conventions;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Shared <c>(role, action)</c> boundary-parsing helper used by both
/// <see cref="ConventionStoreEndpoints"/> and <see cref="PromptEndpoints"/>
/// (I-5: lift TryParsePair out of <see cref="ConventionStoreEndpoints"/> so both
/// surfaces validate the same taxonomy contract at the HTTP boundary).
///
/// <para>
/// Validates two things:
/// <list type="number">
///   <item>The token strings are recognised by
///     <see cref="AgentRoleExtensions.Parse"/> /
///     <see cref="AgentActionExtensions.Parse"/> — unknown token → 400
///     <c>CONVENTION_INVALID_KEY</c>.</item>
///   <item>The pair is a valid taxonomy cell per
///     <see cref="RolePhaseMap.IsRoleEligibleForPhase"/> — known-but-ineligible
///     (e.g. developer/deploy) → 400 <c>CONVENTION_INELIGIBLE_PAIR</c>.</item>
/// </list>
/// This prevents the prompt endpoint from doing a store round-trip and then a 404
/// for a pair that the taxonomy guarantees will never have data, surfacing it as a
/// cleaner 400 (bad input) instead.
/// </para>
/// </summary>
internal static class RoleActionParsing
{
    /// <summary>
    /// Parse and taxonomy-validate a <c>(role, action)</c> pair at the HTTP
    /// boundary. Returns <c>false</c> with a 400 <paramref name="error"/> result
    /// when the token is unknown OR when the pair is known-but-ineligible.
    /// </summary>
    internal static bool TryParsePair(
        string? role,
        string? action,
        out AgentRole parsedRole,
        out AgentAction parsedAction,
        out IResult error)
    {
        parsedRole = default;
        parsedAction = default;
        error = Results.Empty;

        if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(action))
        {
            error = Results.BadRequest(new { error = "Both role and action are required.", code = "CONVENTION_INVALID_KEY" });
            return false;
        }

        try
        {
            parsedRole = AgentRoleExtensions.Parse(role);
            parsedAction = AgentActionExtensions.Parse(action);
        }
        catch (ArgumentException ex)
        {
            error = Results.BadRequest(new { error = ex.Message, code = "CONVENTION_INVALID_KEY" });
            return false;
        }

        // Known tokens but the role doesn't own this action (e.g. developer/deploy):
        // there is no taxonomy cell and hence no system default. Reject up-front as a
        // 400 (bad key) rather than let the caller attempt a store query and get a
        // 404 — this is a malformed request, not a missing resource.
        if (!RolePhaseMap.IsRoleEligibleForPhase(parsedAction.ToWire(), parsedRole.ToWire()))
        {
            error = Results.BadRequest(new
            {
                error = $"(role='{parsedRole.ToWire()}', action='{parsedAction.ToWire()}') is not a valid taxonomy cell.",
                code = "CONVENTION_INELIGIBLE_PAIR",
            });
            return false;
        }

        return true;
    }
}
