namespace Tamma.Api.Authorization;

/// <summary>
/// Endpoint filter that enforces a minimum role within the path-tenant.
/// Must run AFTER <see cref="RequireTenantMembershipFilter"/> which stashes
/// the caller's role on <c>HttpContext.Items["TenantRole"]</c>. Mirrors the
/// inlined role-hierarchy checks in TS
/// <c>packages/api/src/routes/orgs/index.ts</c> for admin-or-higher routes
/// (finding 024 companion).
/// </summary>
public sealed class RequireTenantRoleFilter(string minRole) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext ctx,
        EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var role = http.Items[RequireTenantMembershipFilter.TenantRoleItemKey] as string;

        if (role is null)
        {
            // Filter chain misconfigured: membership filter must run first.
            return Results.Json(
                new { error = "Tenant role not resolved" },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        if (!TenantRoleHierarchy.IsAtLeast(role, minRole))
        {
            var humanRole = minRole switch
            {
                TenantRoleHierarchy.Owner => "owner",
                TenantRoleHierarchy.Admin => "admin role or higher",
                _ => minRole,
            };
            return Results.Json(
                new { error = $"Requires {humanRole}" },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(ctx);
    }
}
