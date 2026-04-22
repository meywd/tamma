using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Tamma.Api.Auth;

/// <summary>
/// Requirement that succeeds when EITHER the authenticated user is acting on
/// their own resource (route param <c>{id}</c> equals their <c>sub</c>) OR
/// they hold the named permission. Mirrors TS
/// <c>requireSelfOrRole</c> in <c>packages/api/src/middleware/require-role.ts</c>.
/// </summary>
public class SelfOrPermissionRequirement(string permission, string routeIdParam = "id")
    : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
    public string RouteIdParam { get; } = routeIdParam;
}

public class SelfOrPermissionHandler(IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<SelfOrPermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SelfOrPermissionRequirement requirement)
    {
        var http = httpContextAccessor.HttpContext;
        if (http is null)
            return Task.CompletedTask;

        // Self check: route id equals the caller's sub claim.
        var routeId = http.Request.RouteValues.TryGetValue(requirement.RouteIdParam, out var v)
            ? v?.ToString()
            : null;

        var sub = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (!string.IsNullOrEmpty(routeId) && !string.IsNullOrEmpty(sub) &&
            string.Equals(routeId, sub, StringComparison.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Permission check: role-derived or API-key-claim-derived.
        var roleClaim = context.User.FindFirst(ClaimTypes.Role)?.Value;
        if (roleClaim is not null && Permissions.HasPermission(roleClaim, requirement.Permission))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
        var permClaims = context.User.FindAll("permission").Select(c => c.Value).ToList();
        if (permClaims.Contains(requirement.Permission) || permClaims.Contains("*"))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
