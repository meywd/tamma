using System.Security.Claims;
using System.Text.Json;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Middleware;

/// <summary>
/// Auto-provision middleware for users without an active tenant. Two paths:
/// (1) user has memberships → pick most-recent and persist it as their
///     active tenant in <c>users.tenant_id</c>;
/// (2) user has no memberships → mint a personal tenant with a TS-compatible
///     <c>u-{8hex}</c> slug, create owner membership, persist active tenant.
///
/// <para>Finding 022 remediation: prior implementation built an email-based
/// slug, never persisted on the existing-membership path (recomputed every
/// request), and emitted no audit events.</para>
/// </summary>
public class EnsurePersonalTenantMiddleware(RequestDelegate next)
{
    private const int MaxSlugAttempts = 5;

    private static readonly HashSet<string> SkipPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/health",
        "/api/v1/auth/register",
        "/api/v1/auth/login",
        "/api/v1/auth/verify-email",
        "/api/v1/auth/resend-verification",
        "/api/v1/auth/password-reset/request",
        "/api/v1/auth/password-reset/confirm",
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
        IUserRepository userRepo,
        IEventRepository events,
        ILogger<EnsurePersonalTenantMiddleware> logger)
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

        // Existing-membership path
        var memberships = await membershipRepo.GetUserTenantsAsync(userId);
        if (memberships.Count > 0)
        {
            var mostRecent = memberships.OrderByDescending(m => m.JoinedAt).First();
            tenantContext.SetTenantId(mostRecent.TenantId);

            // Persist as active tenant so subsequent requests skip the
            // discovery dance (finding 022).
            try
            {
                await userRepo.UpdateActiveTenantAsync(userId, mostRecent.TenantId);
                await EmitEvent(events, "TENANT.RESOLVED.SUCCESS", mostRecent.TenantId, userId,
                    new { reason = "existing_membership" });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist active tenant for user {UserId}", userId);
            }

            await next(context);
            return;
        }

        // Auto-create personal tenant path
        var user = await userRepo.GetByIdAsync(userId);
        if (user is null)
        {
            await next(context);
            return;
        }

        // TS-compatible slug: u-<first 8 hex of userId>, retry on collision.
        var baseSlug = $"u-{userId.ToString("N").Substring(0, 8).ToLowerInvariant()}";
        var slug = baseSlug;
        var attempts = 0;
        while (await tenantRepo.GetBySlugAsync(slug) is not null)
        {
            attempts++;
            slug = $"{baseSlug}-{attempts}";
            if (attempts > MaxSlugAttempts)
            {
                logger.LogError(
                    "Failed to generate unique personal tenant slug for user {UserId}", userId);
                await next(context);
                return;
            }
        }

        var displayName = user.DisplayName ?? user.GitHubLogin ?? user.Email.Split('@')[0];
        var tenant = await tenantRepo.CreateAsync(new Tenant
        {
            Name = $"{displayName}'s Workspace",
            Slug = slug,
            Type = "personal",
            OwnerId = userId,
        });

        await membershipRepo.AddAsync(tenant.Id, userId, "owner");
        await userRepo.UpdateActiveTenantAsync(userId, tenant.Id);
        tenantContext.SetTenantId(tenant.Id);

        try
        {
            await EmitEvent(events, "TENANT.AUTO_CREATED.SUCCESS", tenant.Id, userId,
                new { reason = "first_login", slug });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit TENANT.AUTO_CREATED for user {UserId}", userId);
        }

        await next(context);
    }

    private static async Task EmitEvent(
        IEventRepository events, string type, Guid tenantId, Guid userId, object data)
    {
        await events.AppendAsync(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(new
            {
                tenantId = tenantId.ToString(),
                userId = userId.ToString(),
            }),
            Metadata = JsonSerializer.Serialize(new
            {
                workflowVersion = "1.0.0",
                eventSource = "system",
            }),
            Data = JsonSerializer.Serialize(data),
            CreatedAt = DateTime.UtcNow,
        });
    }
}
