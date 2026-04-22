using System.Security.Claims;
using Tamma.Api.Auth;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Middleware;

/// <summary>
/// Resolves the current tenant from one of four sources, in priority order:
/// (1) <see cref="AuthPrincipal"/> populated by the API-key handler,
/// (2) JWT <c>tenantId</c> / <c>tid</c> claim,
/// (3) <see cref="InstallationAuthPrincipal"/> installation lookup,
/// (4) authenticated user's <c>users.tenant_id</c> column.
///
/// <para>Mirrors the deleted TS
/// <c>packages/api/src/middleware/tenant-context.ts</c>. The shallow JWT-only
/// port is finding 023; this middleware widens resolution and adds the
/// missing user-row fallback so cookie-based dashboard requests carrying a
/// JWT without <c>tid</c> still bind a tenant.</para>
///
/// <para>Note on fail-closed behavior: this middleware does NOT 403 when no
/// source resolves. Production runtime today still relies on EF query
/// filters (RLS is dormant per Phase-2 migration) which already return zero
/// rows for null tenant; surfacing a 403 here would break the personal-
/// tenant bootstrap flow that <see cref="EnsurePersonalTenantMiddleware"/>
/// completes immediately after. The 403-on-unresolved contract from TS will
/// land alongside the connection-string split in Phase-3 (finding 002 / 023
/// notes).</para>
/// </summary>
public class TenantContextMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> TenantFreePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/health",
        "/api/v1/auth/register",
        "/api/v1/auth/login",
        "/api/v1/auth/verify-email",
        "/api/v1/auth/resend-verification",
        "/api/v1/auth/password-reset/request",
        "/api/v1/auth/password-reset/confirm",
        "/api/auth/github",
        "/api/auth/github/callback",
        "/api/github/callback",
        "/api/github/webhooks",
        "/api/convention-templates",
        "/health",
        "/swagger",
    };

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenantContext,
        ITenantRepository tenantRepo,
        IUserRepository userRepo)
    {
        var path = context.Request.Path.Value ?? "";

        if (TenantFreePaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        Guid? resolved = null;

        // Source 1: AuthPrincipal (API-key auth). Tagged-union typed access.
        var principal = context.GetAuthPrincipal();
        if (principal is UserAuthPrincipal up)
        {
            resolved = up.TenantId;
        }
        else if (principal is InstallationAuthPrincipal ip)
        {
            if (ip.TenantId.HasValue)
            {
                resolved = ip.TenantId.Value;
            }
            else
            {
                // Source 3a: look up the installation's tenant via external_id.
                var tenant = await tenantRepo.GetByExternalIdAsync(ip.InstallationId.ToString());
                if (tenant is not null) resolved = tenant.Id;
            }
        }
        else if (principal is ServiceAuthPrincipal sp)
        {
            resolved = sp.TenantId; // already pre-resolved from X-Tenant-Id
        }

        // Source 2: JWT claim. Story 28-9 promoted `active_tenant_id` to the
        // canonical claim name; `tenantId` and `tid` are kept as legacy
        // fallbacks so tokens minted before the rollout still resolve.
        if (resolved is null)
        {
            var tidClaim = context.User.FindFirst("active_tenant_id")?.Value
                ?? context.User.FindFirst("tenantId")?.Value
                ?? context.User.FindFirst("tid")?.Value;
            if (!string.IsNullOrEmpty(tidClaim) && Guid.TryParse(tidClaim, out var fromClaim))
            {
                resolved = fromClaim;
            }
        }

        // Source 4: user-row fallback (JWT lacked tid).
        if (resolved is null)
        {
            var userIdRaw = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (Guid.TryParse(userIdRaw, out var userId))
            {
                var user = await userRepo.GetByIdAsync(userId);
                if (user?.TenantId is not null && user.TenantId.Value != Guid.Empty)
                {
                    resolved = user.TenantId.Value;
                }
            }
        }

        if (resolved.HasValue)
        {
            tenantContext.SetTenantId(resolved.Value);
        }

        await next(context);
    }
}
