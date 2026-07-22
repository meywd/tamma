using System.Diagnostics;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Api.Auth;
using Tamma.Api.Services.TenantStatus;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Repositories;

namespace Tamma.Api.Middleware;

/// <summary>
/// Resolves the current tenant from one of four sources, in priority order:
/// (1) <see cref="AuthPrincipal"/> populated by the API-key handler,
/// (2) JWT <c>active_tenant_id</c> / <c>tenantId</c> / <c>tid</c> claim,
/// (3) <see cref="InstallationAuthPrincipal"/> installation lookup,
/// (4) authenticated user's <c>users.tenant_id</c> column.
///
/// <para>Mirrors the deleted TS
/// <c>packages/api/src/middleware/tenant-context.ts</c>. The shallow JWT-only
/// port is finding 023; this middleware widens resolution and adds the
/// missing user-row fallback so cookie-based dashboard requests carrying a
/// JWT without <c>tid</c> still bind a tenant.</para>
///
/// <para><b>Story 28-8 (per-tenant DB plumbing)</b>: once the tenant id is
/// resolved this middleware ALSO:
/// <list type="bullet">
///   <item><description>Asks <see cref="ITenantConnectionResolver"/> to warm
///     the per-tenant Npgsql data-source pool. This pays the cold-start
///     latency (and the CP round-trip + decrypt) here, BEFORE the request
///     reaches a handler that calls
///     <see cref="ITenantDbContextFactory.CreateAsync"/>. Once the resolver
///     returns, the tenant id stored on <see cref="ITenantContext"/> is
///     guaranteed to map to a usable warm pool.</description></item>
///   <item><description>Adds an <see cref="Activity"/> baggage tag
///     <c>tamma.tenant_id</c> so distributed traces can attribute spans to
///     a tenant without leaking it into logs / structured metadata
///     elsewhere. Falls back to a tag on the current activity if no
///     baggage support is wired (no allocations on null-Activity hosts).
///     </description></item>
///   <item><description>Fails fast with HTTP 401 when the JWT carries a
///     tenant claim but the resolver throws
///     <see cref="TenantNotFoundException"/> — a stale token whose tenant
///     was deleted is no longer trustworthy and must trigger a re-login
///     rather than silently fall through to the personal-tenant bootstrap
///     flow. <see cref="TenantNotProvisionedException"/> is treated the
///     same way: the principal-asserted tenant exists but is not in a
///     state we can serve from, and the request must surface that to the
///     client immediately.</description></item>
/// </list></para>
///
/// <para>Note on fail-closed behavior for unresolved tenants: this middleware
/// still does NOT 403 when no source resolves a tenant id (the next
/// middleware in the pipeline,
/// <see cref="EnsurePersonalTenantMiddleware"/>, owns the personal-tenant
/// bootstrap path). The fail-fast above is scoped to the case where a
/// resolver lookup ACTIVELY failed, not where no claim was present.</para>
/// </summary>
public class TenantContextMiddleware(RequestDelegate next)
{
    /// <summary>
    /// OpenTelemetry baggage / activity-tag key for the resolved tenant id.
    /// Public + const so downstream activities and tests can reference the
    /// same name without string drift.
    /// </summary>
    public const string TenantBaggageKey = "tamma.tenant_id";

    /// <summary>
    /// Path prefixes that bypass tenant resolution entirely. Anything that
    /// runs BEFORE a tenant exists (registration, login, password reset)
    /// or that is deliberately platform-scoped (admin, GitHub webhooks,
    /// health probes, convention templates) goes here.
    ///
    /// <para>Story 28-8 widens the original list to cover the
    /// <c>/api/v1/admin/*</c> control-plane surface that ships in
    /// Stories 28-7 / 28-11 — those endpoints intentionally operate
    /// across tenants and must not bind one.</para>
    /// </summary>
    private static readonly string[] TenantFreePathPrefixes =
    {
        "/api/health",
        // Admin (control-plane) surface — both the legacy `/api/admin/`
        // group (today's Program.cs MapGroup) and the planned
        // `/api/v1/admin/*` cutover from Story 28-7. Either prefix bypasses
        // tenant binding because the admin endpoints intentionally operate
        // across tenants.
        "/api/admin/",
        "/api/v1/admin/",
        // Auth surface — registration, login, password reset all run BEFORE
        // a tenant exists. Cover both `/api/v1/auth/*` (current) and the
        // legacy `/api/auth/*` shape kept around for back-compat.
        "/api/v1/auth/",
        "/api/auth/",
        "/api/github/callback",
        "/api/github/webhooks",
        "/api/convention-templates",
        "/health",
        "/swagger",
        // Story 39-18 — the real-time channel hubs. A hub connection self-scopes:
        // OnConnectedAsync derives the tenant (and, on the user hub, the user) from
        // the authenticated principal's claims and joins the corresponding group.
        // The SignalR negotiate/connect requests must therefore bypass DB-backed
        // tenant resolution (which would 404 `tenant_not_found` before the hub ever
        // runs) — the same self-scoping posture as the engine callbacks.
        "/hubs",
    };

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenantContext,
        ITenantRepository tenantRepo,
        IUserRepository userRepo,
        ITenantConnectionResolver connectionResolver,
        ITenantStatusCache statusCache,
        ControlPlaneDbContext controlPlane,
        ILogger<TenantContextMiddleware> logger)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (IsTenantFreePath(path))
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var (resolved, source) = await ResolveTenantIdAsync(
            context, tenantRepo, userRepo).ConfigureAwait(false);

        if (!resolved.HasValue)
        {
            // No source produced a tenant id. Defer to
            // EnsurePersonalTenantMiddleware which owns the personal-tenant
            // bootstrap path.
            await next(context);
            return;
        }

        var tenantId = resolved.Value;

        // Story 28-8 H7 — short-TTL status cache. On hit, gate the
        // request with the proper non-active HTTP code WITHOUT touching
        // CP. On miss, fall through to a single CP read that BOTH
        // populates the cache AND short-circuits non-active states.
        // Active / null statuses fall through to the resolver warm-up.
        //
        // Phase 4 (unified tenancy): the gate is verb-aware —
        // `draining` (tenant move in progress, brief read-only window)
        // lets safe verbs (GET/HEAD/OPTIONS) through and 503s mutations
        // with Retry-After: 5.
        if (statusCache.TryGet(tenantId, out var cachedStatus))
        {
            if (!TenantStatusEvaluator.AllowsRequest(cachedStatus, context.Request.Method))
            {
                logger.LogWarning(
                    "tenant.middleware.status_gate_cached tenantId={TenantId} status={Status} source={Source} path={Path}",
                    tenantId, cachedStatus, source, path);
                await TenantStatusEvaluator
                    .WriteNonActiveResponseAsync(context, tenantId, cachedStatus, context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            }
        }
        else
        {
            // Cold path: read tenants.Status (shadow column) once + cache
            // the result. EF.Property keeps us free of an entity
            // contract dependency for the shadow column.
            var row = await controlPlane.Tenants
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(t => t.Id == tenantId)
                .Select(t => new { t.DeletedAt, Status = EF.Property<string?>(t, "Status") })
                .FirstOrDefaultAsync(context.RequestAborted)
                .ConfigureAwait(false);

            if (row is null)
            {
                // Stale JWT pointing at a vanished tenant. Cache the
                // not-found marker so a flood of probes from the same
                // stale token doesn't hammer CP.
                statusCache.Set(tenantId, "not_found");
                logger.LogWarning(
                    "tenant.middleware.unknown_tenant tenantId={TenantId} source={Source} path={Path}",
                    tenantId, source, path);
                await TenantStatusEvaluator
                    .WriteNonActiveResponseAsync(context, tenantId, status: "not_found", context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            }

            // Soft-deleted rows behave as "deleted" regardless of Status.
            var effectiveStatus = row.DeletedAt is not null
                ? TenantStatusEvaluator.StatusDeleted
                : row.Status;
            statusCache.Set(tenantId, effectiveStatus);

            if (!TenantStatusEvaluator.AllowsRequest(effectiveStatus, context.Request.Method))
            {
                logger.LogWarning(
                    "tenant.middleware.status_gate tenantId={TenantId} status={Status} source={Source} path={Path}",
                    tenantId, effectiveStatus, source, path);
                await TenantStatusEvaluator
                    .WriteNonActiveResponseAsync(context, tenantId, effectiveStatus, context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            }
        }

        // Pre-warm the per-tenant Npgsql pool. Resolver throws fast for
        // unknown / deleted / un-provisioned tenants — we translate those
        // into HTTP responses BEFORE binding the request scope so handlers
        // never see a half-bound context.
        try
        {
            // Result intentionally discarded — the resolver caches the
            // NpgsqlDataSource internally. We just need the side-effect of
            // building (and validating) the per-tenant pool.
            _ = await connectionResolver
                .GetDataSourceAsync(tenantId, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (TenantNotFoundException)
        {
            // Race: the row vanished between the status read above and
            // the resolver lookup. Invalidate the cache, write the same
            // shape as the not-found path, and bail.
            statusCache.Invalidate(tenantId);
            logger.LogWarning(
                "tenant.middleware.unknown_tenant_race tenantId={TenantId} source={Source} path={Path}",
                tenantId, source, path);
            await TenantStatusEvaluator
                .WriteNonActiveResponseAsync(context, tenantId, status: "not_found", context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }
        catch (TenantNotProvisionedException ex)
        {
            // Race: status flipped between our cache read and the resolver
            // lookup. Mirror the cache to the new value + return the
            // proper non-active response.
            statusCache.Set(tenantId, ex.Status);
            logger.LogWarning(
                "tenant.middleware.tenant_not_provisioned tenantId={TenantId} status={Status} source={Source} path={Path}",
                tenantId, ex.Status, source, path);
            await TenantStatusEvaluator
                .WriteNonActiveResponseAsync(context, tenantId, ex.Status, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        tenantContext.SetTenantId(tenantId);

        // Stamp the current Activity for distributed tracing. Activity is
        // null when no listener is registered (typical in unit tests with
        // no host) — guard with `?.` so we stay allocation-free in that
        // case. Baggage propagates across process boundaries; the duplicate
        // tag gives in-process consumers (Serilog enricher, ETW listener)
        // an O(1) lookup without walking the baggage list.
        var activity = Activity.Current;
        if (activity is not null)
        {
            var tenantString = tenantId.ToString();
            activity.AddBaggage(TenantBaggageKey, tenantString);
            activity.SetTag(TenantBaggageKey, tenantString);
        }

        await next(context);
    }

    /// <summary>
    /// Returns true iff <paramref name="path"/> matches the bypass list.
    /// Prefix match (case-insensitive) so any sub-route under e.g.
    /// <c>/api/v1/admin/</c> is exempt.
    /// </summary>
    internal static bool IsTenantFreePath(string path)
    {
        foreach (var prefix in TenantFreePathPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task<(Guid? TenantId, string Source)> ResolveTenantIdAsync(
        HttpContext context,
        ITenantRepository tenantRepo,
        IUserRepository userRepo)
    {
        // Source 1: AuthPrincipal (API-key auth). Tagged-union typed access.
        var principal = context.GetAuthPrincipal();
        if (principal is UserAuthPrincipal up)
            return (up.TenantId, "api_key_user");

        if (principal is InstallationAuthPrincipal ip)
        {
            if (ip.TenantId.HasValue)
                return (ip.TenantId.Value, "api_key_installation");

            // Look up the installation's tenant via external_id.
            var tenant = await tenantRepo
                .GetByExternalIdAsync(ip.InstallationId.ToString())
                .ConfigureAwait(false);
            if (tenant is not null)
                return (tenant.Id, "api_key_installation_lookup");
        }

        if (principal is ServiceAuthPrincipal sp && sp.TenantId.HasValue)
            return (sp.TenantId.Value, "api_key_service");

        // Source 2: JWT claim. Story 28-9 promoted `active_tenant_id` to
        // the canonical claim name; `tenantId` and `tid` are kept as
        // legacy fallbacks so tokens minted before the rollout still
        // resolve.
        var tidClaim = context.User.FindFirst("active_tenant_id")?.Value
            ?? context.User.FindFirst("tenantId")?.Value
            ?? context.User.FindFirst("tid")?.Value;
        if (!string.IsNullOrEmpty(tidClaim) && Guid.TryParse(tidClaim, out var fromClaim))
            return (fromClaim, "jwt_claim");

        // Source 4: user-row fallback (JWT lacked tid).
        if (context.User.GetUserId() is Guid userId)
        {
            var user = await userRepo.GetByIdAsync(userId).ConfigureAwait(false);
            if (user?.TenantId is not null && user.TenantId.Value != Guid.Empty)
                return (user.TenantId.Value, "user_row_fallback");
        }

        return (null, "none");
    }

}
