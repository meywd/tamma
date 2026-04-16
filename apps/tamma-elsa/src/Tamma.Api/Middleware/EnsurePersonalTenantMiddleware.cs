using System.Security.Claims;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Middleware;

public class EnsurePersonalTenantMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> SkipPaths = new(StringComparer.OrdinalIgnoreCase)
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
        ITenantMembershipRepository membershipRepo,
        IUserRepository userRepo)
    {
        var path = context.Request.Path.Value ?? "";
        if (SkipPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        // Already has tenant? Continue
        if (tenantContext.TenantId.HasValue)
        {
            await next(context);
            return;
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            await next(context);
            return;
        }

        // Check existing memberships
        var memberships = await membershipRepo.GetUserTenantsAsync(userId);
        if (memberships.Count > 0)
        {
            var mostRecent = memberships.OrderByDescending(m => m.JoinedAt).First();
            tenantContext.SetTenantId(mostRecent.TenantId);
            await next(context);
            return;
        }

        // Auto-create personal tenant
        var user = await userRepo.GetByIdAsync(userId);
        if (user is null)
        {
            await next(context);
            return;
        }

        var slug = user.Email.Split('@')[0].ToLowerInvariant().Replace(".", "-").Replace("+", "-");
        var tenant = await tenantRepo.CreateAsync(new Tenant
        {
            Name = user.DisplayName ?? user.Email,
            Slug = $"personal-{slug}-{Guid.NewGuid().ToString()[..8]}",
            Type = "personal",
            OwnerId = userId
        });

        await membershipRepo.AddAsync(tenant.Id, userId, "owner");
        await userRepo.UpdateActiveTenantAsync(userId, tenant.Id);
        tenantContext.SetTenantId(tenant.Id);

        await next(context);
    }
}
