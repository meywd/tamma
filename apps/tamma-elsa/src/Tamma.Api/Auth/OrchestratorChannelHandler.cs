using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Tamma.Api.Endpoints;

namespace Tamma.Api.Auth;

/// <summary>
/// Story 39-18 (D10) — the requirement gating the workflow↔orchestrator hub
/// (<c>/hubs/orchestrator</c>). Authenticated AND (a service principal OR the 39-8 D6
/// orchestrator claim). A tenant member/admin/owner JWT — which authenticates but is
/// neither — FAILS it (AC5's "reject non-orchestrator principals"). Until 39-17 mints
/// the orchestrator claim, tests mint it directly (the 39-8 pattern).
/// </summary>
public class OrchestratorChannelRequirement : IAuthorizationRequirement;

/// <summary>
/// Authorization handler for <see cref="OrchestratorChannelRequirement"/>. Succeeds
/// for the same trust class as <c>EngineServiceOnly</c> (a resolved
/// <see cref="ServiceAuthPrincipal"/> or the platform <c>"*"</c> permission claim) OR
/// a principal carrying <c>tamma:principal-type = orchestrator</c>.
/// </summary>
public class OrchestratorChannelHandler(IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<OrchestratorChannelRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OrchestratorChannelRequirement requirement)
    {
        // The orchestrator agent claim (39-8 D6) — the primary signal for the 39-17
        // resident agent + the hub tests.
        var principalType = context.User.FindFirst(ApprovalChannels.PrincipalTypeClaim)?.Value;
        if (string.Equals(principalType, ApprovalChannels.OrchestratorPrincipalType, StringComparison.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Service-principal trust class (mirrors ServicePrincipalHandler): a resolved
        // ServiceAuthPrincipal or the platform-wide "*" permission claim.
        if (httpContextAccessor.HttpContext?.GetAuthPrincipal() is ServiceAuthPrincipal)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var permClaims = context.User.FindAll("permission").Select(c => c.Value);
        if (permClaims.Contains("*"))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
