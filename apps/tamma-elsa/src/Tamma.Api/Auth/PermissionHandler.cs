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

        // Story 28-R2 / Finding C1 — a platform admin is a superuser. Granting
        // every PermissionRequirement when the JWT carries
        // platformRole=platform_admin keeps the platform-admin happy path
        // working when an /api/admin/* route is composed of (group:
        // AdminAccess) + (route: PlatformOwnerAccess) — both gates resolve to
        // "yes" without requiring the operator to also hold a per-tenant
        // admin/owner role. Without this rule, a platform admin who is a
        // mere `member` of every tenant they belong to could not reach
        // /api/admin/* at all (the AdminAccess gate would 403 first).
        var platformRole = context.User.FindFirst("platformRole")?.Value;
        if (string.Equals(platformRole, "platform_admin", StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Story 28-R2 / Finding C1 — requirement that the caller's JWT carries a
/// specific <c>platformRole</c> claim value (e.g. <c>"platform_admin"</c>).
/// This is distinct from <see cref="PermissionRequirement"/>, which keys off
/// the per-tenant <c>role</c> claim and the <see cref="Permissions"/> matrix.
///
/// <para>The legacy <c>OwnerAccess</c> policy (mapped to <c>users:manage</c>)
/// fires for any user with role <c>owner</c> in any tenant. Every signed-up
/// user is auto-<c>owner</c> of their personal tenant, so that policy let
/// every user pass platform-scoped admin routes. <c>PlatformOwnerAccess</c>
/// (using this requirement) is the platform-scoped replacement gate.</para>
/// </summary>
public class PlatformPermissionRequirement(string requiredPlatformRole) : IAuthorizationRequirement
{
    /// <summary>
    /// Required value of the JWT <c>platformRole</c> claim. Comparison is
    /// ordinal case-sensitive — the claim is minted by
    /// <see cref="JwtService.GenerateAccessToken"/> as a lowercase string
    /// (<c>"user"</c> or <c>"platform_admin"</c>), so any drift in the
    /// minting layer fails closed (which is the safe direction).
    /// </summary>
    public string RequiredPlatformRole { get; } = requiredPlatformRole;
}

/// <summary>
/// Authorization handler for <see cref="PlatformPermissionRequirement"/>.
/// Inspects the JWT <c>platformRole</c> claim (Story 28-R2 / C1) and succeeds
/// the requirement when the value matches. Also accepts an API-key
/// <c>permission</c> claim of <c>"*"</c> as a service-key escape hatch — this
/// matches the existing <see cref="PermissionHandler"/> pattern so platform
/// service keys (Elsa→API, BFF→API) keep working without a per-route grant
/// list. There is intentionally NO match against the per-tenant <c>role</c>
/// claim — that's the bug C1 closed.
/// </summary>
public class PlatformPermissionHandler : AuthorizationHandler<PlatformPermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PlatformPermissionRequirement requirement)
    {
        // Primary path — read the dedicated platformRole claim minted by
        // JwtService. The claim name is "platformRole" (no URI prefix; we
        // call handler.OutboundClaimTypeMap.Clear() in JwtService).
        var platformRole = context.User.FindFirst("platformRole")?.Value;
        if (!string.IsNullOrWhiteSpace(platformRole)
            && string.Equals(platformRole, requirement.RequiredPlatformRole, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Service-key escape hatch — an API key with permission "*" is the
        // platform-wide superuser key (matches the existing PermissionHandler
        // contract). Story 16-7 explicitly minted these as owner-scoped, so
        // honouring them here keeps Elsa→API + BFF→API flows working.
        var permClaims = context.User.FindAll("permission").Select(c => c.Value).ToList();
        if (permClaims.Contains("*"))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
