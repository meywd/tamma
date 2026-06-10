using System.Security.Claims;
using Tamma.Api.Auth;
using Tamma.Data.Repositories;

namespace Tamma.Api.Authorization;

/// <summary>
/// Endpoint filter that asserts the authenticated caller has a membership
/// row in the path-tenant (<c>{tenantId}</c> route value) of the current
/// request. Mirrors the deleted TS
/// <c>packages/api/src/middleware/require-tenant.ts</c> preHandler
/// (finding 024) and plugs the cross-tenant-access hole in finding 001.
///
/// <para>Behavior:</para>
/// <list type="number">
///   <item>401 if the request is not authenticated.</item>
///   <item>400 if the route has no <c>tenantId</c> segment / it is not a
///   valid GUID.</item>
///   <item>403 if the authenticated user has no membership row for the
///   path tenant.</item>
///   <item>On success, stashes the resolved role in
///   <c>HttpContext.Items["TenantRole"]</c> so sibling filters
///   (<see cref="RequireTenantRoleFilter"/>) can enforce role-hierarchy
///   without an extra DB round-trip.</item>
/// </list>
/// </summary>
public sealed class RequireTenantMembershipFilter(
    ITenantMembershipRepository membershipRepo) : IEndpointFilter
{
    public const string TenantRoleItemKey = "TenantRole";
    public const string PathTenantIdItemKey = "PathTenantId";

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext ctx,
        EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;

        if (http.User.Identity?.IsAuthenticated != true)
        {
            return Results.Json(
                new { error = "Not authenticated" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // Route value "tenantId" (path-tenant endpoints) — canonical.
        if (!http.Request.RouteValues.TryGetValue("tenantId", out var raw)
            || raw is null
            || !Guid.TryParse(raw.ToString(), out var pathTenantId))
        {
            return Results.BadRequest(new { error = "Missing or invalid tenantId route value" });
        }

        if (http.User.GetUserId() is not Guid userId)
        {
            return Results.Json(
                new { error = "Not authenticated" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var role = await membershipRepo.GetRoleAsync(pathTenantId, userId);
        if (role is null)
        {
            return Results.Json(
                new { error = "Not a member of this organization" },
                statusCode: StatusCodes.Status403Forbidden);
        }

        http.Items[TenantRoleItemKey] = role;
        http.Items[PathTenantIdItemKey] = pathTenantId;

        return await next(ctx);
    }
}
