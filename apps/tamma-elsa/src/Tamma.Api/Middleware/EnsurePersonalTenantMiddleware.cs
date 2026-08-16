using System.Security.Claims;
using Microsoft.Extensions.Options;
using Tamma.Api.Auth;
using System.Text.Json;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Data.Seeders;

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
///
/// <para><b>Story 28-8 (2026-05-30 audit-residual closure) — mode awareness.</b>
/// AC1 of Story 28-8 calls for the "former synchronous-create path" to be
/// eliminated. The pragmatic resolution: this middleware <b>survives in the
/// pipeline only to serve <see cref="TammaMode.SingleUser"/> deployments</b>
/// (self-hosted single-user mode where there is no async-provisioning
/// surface and a personal tenant must materialise on first authenticated
/// request). In <see cref="TammaMode.SaaS"/> the middleware short-circuits
/// before touching any repository — SaaS tenant creation is owned by the
/// async <c>CreateTenantWorkflow</c> (Story 28-5) at registration /
/// verify-email time, NOT by a per-request middleware. A SaaS-mode user
/// arriving here without an active tenant is the AC1 "no_active_tenant"
/// case and falls through to the downstream pipeline (handlers will
/// surface the appropriate 409).</para>
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
        ITammaModeProvider modeProvider,
        ILogger<EnsurePersonalTenantMiddleware> logger)
    {
        // Story 28-8 AC1 — SaaS mode never auto-creates tenants here.
        // The whole middleware is a no-op so SaaS pipelines pay zero cost
        // beyond the mode check. Tenant creation in SaaS is async via
        // CreateTenantWorkflow (Story 28-5).
        if (modeProvider.Mode == TammaMode.SaaS)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        if (SkipPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
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

        Guid userId;
        var claimsLess = false;
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.GetUserId() is Guid claimUserId)
        {
            userId = claimUserId;
        }
        else if (modeProvider.Mode == TammaMode.SingleUser)
        {
            claimsLess = true;
            // 2026-08-13 (engine-driven E2E, single-user mode-awareness):
            // service-plane calls (the engine's Tamma:ApiToken mediation
            // requests — llm/call, git callbacks, event drain) carry NO user
            // claim, so the old IsAuthenticated gate left them tenant-less
            // FOREVER, and every tenant-resident read they trigger
            // (acceptance_rules_overrides via the 43-x autonomy gate,
            // document instances, conventions overrides) threw "requires an
            // ambient tenant id" — the autonomy gate then failed CLOSED and
            // every engine LLM call answered 500. In single-user mode the
            // principal of a service call IS the sole user (the same
            // resolution SoleUserProvider gives the gate itself), so bind —
            // and if need be mint — their personal tenant here, exactly like
            // a first authenticated dashboard request would. SaaS behavior
            // is untouched (the mode short-circuit above). Pre-setup
            // deployments (no resolvable sole user / no users row) fall
            // through tenant-less as before.
            var soleUsers = context.RequestServices
                .GetService<Tamma.Api.Services.Actions.ISoleUserProvider>();
            if (soleUsers is null)
            {
                await next(context);
                return;
            }
            try
            {
                userId = await soleUsers.GetSoleUserIdAsync(context.RequestAborted);
            }
            catch
            {
                // No configured owner and no users row — pre-setup deployment.
                await next(context);
                return;
            }
        }
        else
        {
            await next(context);
            return;
        }

        if (claimsLess)
        {
            // The CLAIMS-LESS (service-plane) binding is best-effort and MUST
            // be invisible to hosts where it cannot work: auth-pipeline pins
            // ("401 without the handler being reached") run against fixtures
            // with no reachable database, and a thrown DbException here would
            // convert their expected 401 into a 500. Any failure ⇒ proceed
            // tenant-less, exactly as before this branch existed.
            try
            {
                await BindOrMintAsync(
                    context, tenantContext, tenantRepo, membershipRepo, userRepo, events,
                    logger, userId);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "Single-user service-plane tenant binding unavailable; proceeding tenant-less");
            }
        }
        else
        {
            // The authenticated first-login path keeps its established
            // failure contract: a provisioning failure PROPAGATES (an
            // unprovisioned tenant cannot access tenant data — failing the
            // first request with the real error beats a broken half-tenant).
            await BindOrMintAsync(
                context, tenantContext, tenantRepo, membershipRepo, userRepo, events,
                logger, userId);
        }

        await next(context);
    }

    private async Task BindOrMintAsync(
        HttpContext context,
        ITenantContext tenantContext,
        ITenantRepository tenantRepo,
        ITenantMembershipRepository membershipRepo,
        IUserRepository userRepo,
        IEventRepository events,
        ILogger<EnsurePersonalTenantMiddleware> logger,
        Guid userId)
    {
        // Existing-membership path
        var memberships = await membershipRepo.GetUserTenantsAsync(userId);
        if (memberships.Count > 0)
        {
            var mostRecent = memberships.OrderByDescending(m => m.JoinedAt).First();
            tenantContext.SetTenantId(mostRecent.TenantId);

            // Persist as active tenant so subsequent requests skip the
            // discovery dance (finding 022).
            //
            // 2026-08-14: ONCE per (user, tenant), not once per request. The
            // claims-less single-user branch runs on every tenant-less
            // service-plane call — hundreds per cycle — and each one was doing a
            // users UPDATE plus a TENANT.RESOLVED.SUCCESS domain-event append
            // for a value that had not changed, flooding the audit trail the
            // event store exists to keep readable. The first binding still
            // writes and still emits; later identical bindings are silent.
            if (AlreadyBound(userId, mostRecent.TenantId)) return;

            try
            {
                await userRepo.UpdateActiveTenantAsync(userId, mostRecent.TenantId);
                await EmitEvent(events, "TENANT.RESOLVED.SUCCESS", mostRecent.TenantId, userId,
                    new { reason = "existing_membership" });
                MarkBound(userId, mostRecent.TenantId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist active tenant for user {UserId}", userId);
            }

            return;
        }

        // Auto-create personal tenant path. Serialized: service-plane calls
        // (the single-user binding above) arrive in PARALLEL bursts from the
        // engine, and two concurrent minters would create two personal tenants
        // for the same sole user (the slug-retry loop happily side-steps the
        // collision). One mints; the rest re-check and bind. The lock is
        // RELEASED before the downstream pipeline runs.
        await MintLock.WaitAsync(context.RequestAborted);
        try
        {
            var remint = await membershipRepo.GetUserTenantsAsync(userId);
            if (remint.Count > 0)
            {
                var won = remint.OrderByDescending(m => m.JoinedAt).First().TenantId;
                tenantContext.SetTenantId(won);
            }
            else
            {
                await MintPersonalTenantAsync(
                    context, tenantContext, tenantRepo, membershipRepo, userRepo, events,
                    logger, userId);
            }
        }
        finally
        {
            MintLock.Release();
        }
    }

    /// <summary>
    /// The (user, tenant) pairs whose active-tenant row has already been
    /// persisted by this process. Bounded by the number of principals a host
    /// serves (exactly one in single-user mode, which is the only mode that
    /// reaches the claims-less branch), so it cannot grow unboundedly. Purely an
    /// optimisation: a cold process simply writes once more.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid User, Guid Tenant), byte>
        BoundActiveTenants = new();

    private static bool AlreadyBound(Guid userId, Guid tenantId) =>
        BoundActiveTenants.ContainsKey((userId, tenantId));

    private static void MarkBound(Guid userId, Guid tenantId) =>
        BoundActiveTenants[(userId, tenantId)] = 0;

    private static readonly SemaphoreSlim MintLock = new(1, 1);

    private async Task MintPersonalTenantAsync(
        HttpContext context,
        ITenantContext tenantContext,
        ITenantRepository tenantRepo,
        ITenantMembershipRepository membershipRepo,
        IUserRepository userRepo,
        IEventRepository events,
        ILogger<EnsurePersonalTenantMiddleware> logger,
        Guid userId)
    {
        var user = await userRepo.GetByIdAsync(userId);
        if (user is null)
        {
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

        // Unified-tenancy Phase 3: the personal tenant is provisioned
        // synchronously (placement → role → schema → minted connection string →
        // migrations) so it is a first-class tenant from its first request.
        // Failure policy: propagate. The Phase 2 stub resolver (shared-path
        // fallback) is gone — an unprovisioned tenant cannot access tenant
        // data at all, so failing the first request with the real error
        // beats limping on with a broken half-tenant.
        //
        // 2026-08-13 (engine-driven E2E): provision BEFORE the membership /
        // users.tenant_id / ambient binding. The old order persisted
        // users.tenant_id first, so PARALLEL requests resolved the tenant via
        // TenantContextMiddleware source (4) mid-provision and threw
        // TenantConnectionStringMissingException ("marked active but has no
        // encrypted connection string") until the envelope landed; a provision
        // FAILURE then left a permanently broken half-bound tenant. Now nothing
        // points at the tenant until it is fully provisioned, and a failed
        // provision leaves only an unreferenced row (the retry mints a fresh
        // slug-suffixed tenant).
        var provisioning = context.RequestServices
            .GetRequiredService<ITenantProvisioningService>();
        await provisioning.ProvisionAsync(tenant.Id, context.RequestAborted);

        await membershipRepo.AddAsync(tenant.Id, userId, "owner");
        await userRepo.UpdateActiveTenantAsync(userId, tenant.Id);
        tenantContext.SetTenantId(tenant.Id);

        // Story 32-16 (AC10) — seed the fresh single-user principal with the
        // platform default persona enabled (insert-missing-only) so the catalog
        // is usable out of the box. Single-user enablement is USER-keyed (the sole
        // user is the principal), so seed by userId — not tenant.Id. Best-effort:
        // a seed failure must not break first-login (the seeder is idempotent and
        // a missing default persona is WARN-logged + skipped inside the seeder).
        try
        {
            var cpDb = context.RequestServices.GetRequiredService<ControlPlaneDbContext>();
            var personaName = context.RequestServices
                .GetRequiredService<IOptions<DefaultPersonaOptions>>().Value.DefaultPersonaName;
            await TenantEnablementSeeder.SeedDefaultPersonaAsync(
                cpDb, personaName, tenantId: null, userId: userId, logger: logger,
                cancellationToken: context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to seed default-persona enablement for user {UserId} (non-fatal)", userId);
        }

        try
        {
            await EmitEvent(events, "TENANT.AUTO_CREATED.SUCCESS", tenant.Id, userId,
                new { reason = "first_login", slug });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit TENANT.AUTO_CREATED for user {UserId}", userId);
        }
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
