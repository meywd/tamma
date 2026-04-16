using System.Security.Claims;
using Tamma.Data;

namespace Tamma.Api.Middleware;

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

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip tenant resolution for public paths
        if (TenantFreePaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        // Not authenticated? Let auth handle it
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        // Extract tenant ID from JWT claim
        var tidClaim = context.User.FindFirst("tid")?.Value;
        if (tidClaim is not null && Guid.TryParse(tidClaim, out var tenantId))
        {
            tenantContext.SetTenantId(tenantId);
        }

        await next(context);
    }
}
