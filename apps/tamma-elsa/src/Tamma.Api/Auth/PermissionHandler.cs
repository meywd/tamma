using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Tamma.Api.Auth;

public class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var roleClaim = context.User.FindFirst(ClaimTypes.Role)?.Value;
        if (roleClaim is not null && Permissions.HasPermission(roleClaim, requirement.Permission))
        {
            context.Succeed(requirement);
        }

        // Also check API key permissions
        var permClaims = context.User.FindAll("permission").Select(c => c.Value).ToList();
        if (permClaims.Contains(requirement.Permission) || permClaims.Contains("*"))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
