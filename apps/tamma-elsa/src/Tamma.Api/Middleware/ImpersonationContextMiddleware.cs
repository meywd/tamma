using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Auth;

namespace Tamma.Api.Middleware;

/// <summary>
/// Story 28-R2 follow-up B — request-scoped gate that converts the JWT
/// <c>imp_id</c> claim into a verified impersonation context exposed
/// via <see cref="HttpContext.Items"/>.
///
/// <para>Pipeline placement: AFTER
/// <see cref="Microsoft.AspNetCore.Builder.AuthAppBuilderExtensions.UseAuthentication"/>
/// (so <see cref="HttpContext.User"/> is populated) and AFTER
/// <see cref="Microsoft.AspNetCore.Builder.AuthorizationAppBuilderExtensions.UseAuthorization"/>
/// (so the policy gate has already accepted the token's signature). It runs
/// alongside (independent of) <see cref="TenantContextMiddleware"/> — the
/// two are orthogonal: tenant-context binds the active tenant; this
/// middleware binds the audit linkage back to the impersonation row.</para>
///
/// <para>Three things happen here:
/// <list type="number">
///   <item><description><b>Validate.</b> If <c>imp_id</c> is present, the
///     row must still be active (<c>ended_at IS NULL</c>) AND must not
///     have been kept open past the configured
///     <c>Tamma:Impersonation:MaxSessionMinutes</c> outer wall.
///     Otherwise the request is rejected 401 — a stale impersonation
///     token must trigger re-authentication, not silently downgrade
///     to whatever role the JWT also carries.</description></item>
///   <item><description><b>Stash.</b> On success, store the impersonation
///     id in <c>HttpContext.Items[ImpersonationIdItem]</c> so downstream
///     handlers (audit-event constructors, log enrichers) can read it
///     without re-querying the DB.</description></item>
///   <item><description><b>Surface.</b> Set a short-lived response header
///     <c>X-Impersonation-Id</c> on the way out so the dashboard can
///     render the "currently impersonating" banner without parsing the
///     JWT itself. Header is opt-in — it's only set when the request was
///     actually inside an impersonation session.</description></item>
/// </list></para>
///
/// <para><b>Defence-in-depth:</b> the middleware does NOT trust the JWT
/// claim alone — it re-reads the row from the CP DB to confirm the
/// session is still active. This means a "revoke" by another
/// platform-admin (Story 28-R2 follow-up B/Phase 2) is honoured on the
/// very next request, not eventually. The cost is a single CP round-trip
/// per impersonated request; for the admin-cohort traffic this is a
/// rounding error.</para>
/// </summary>
public sealed class ImpersonationContextMiddleware
{
    /// <summary>
    /// Key under which the verified <see cref="System.Guid"/> impersonation
    /// id is stored in <see cref="HttpContext.Items"/>. <see cref="string"/>
    /// to match the framework convention; consumers cast back to
    /// <see cref="System.Guid"/>.
    /// </summary>
    public const string ImpersonationIdItem = "ImpersonationId";

    /// <summary>
    /// Response header surfaced to the dashboard so it can render the
    /// "currently impersonating" banner without parsing the JWT.
    /// </summary>
    public const string ImpersonationHeader = "X-Impersonation-Id";

    /// <summary>JWT claim name carrying the impersonation row id.</summary>
    public const string ImpersonationClaim = "imp_id";

    private readonly RequestDelegate _next;

    public ImpersonationContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IAdminImpersonationService impersonationService,
        IConfiguration config,
        TimeProvider timeProvider,
        ILogger<ImpersonationContextMiddleware> logger)
    {
        var claim = context.User?.FindFirst(ImpersonationClaim)?.Value;
        if (string.IsNullOrEmpty(claim))
        {
            await _next(context);
            return;
        }

        if (!Guid.TryParse(claim, out var impId))
        {
            // Malformed claim — fail closed. A token with a junk imp_id
            // is either forged or tampered; the right move is to refuse
            // the request, not to silently strip the claim.
            logger.LogWarning(
                "impersonation.middleware.malformed_claim raw={RawClaim}",
                claim);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "invalid_impersonation_token",
                message = "imp_id claim is not a valid GUID.",
            });
            return;
        }

        var row = await impersonationService
            .GetActiveByIdAsync(impId, context.RequestAborted)
            .ConfigureAwait(false);
        if (row is null)
        {
            // Row doesn't exist OR has been ended. Either way the JWT is
            // no longer trustworthy for impersonation. Return 401 so the
            // dashboard kicks the user back to the platform-admin login.
            logger.LogWarning(
                "impersonation.middleware.session_inactive impId={ImpId}",
                impId);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "impersonation_session_inactive",
                message = "The impersonation session has ended; re-authenticate to continue.",
            });
            return;
        }

        // Outer-session-window check. The 15-min JWT cap handles the
        // common case (token expiry trips the JwtBearer handler before
        // we even get here). The MaxSessionMinutes bound exists for the
        // edge case where an operator chains short tokens — the OUTER
        // wall (StartedAt + MaxSessionMinutes) is the SOC2-mandated cap.
        var maxMinutes = config.GetValue<int?>("Tamma:Impersonation:MaxSessionMinutes") ?? 60;
        if (maxMinutes < 15) maxMinutes = 15;
        if (maxMinutes > 24 * 60) maxMinutes = 24 * 60;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (now > row.StartedAt.AddMinutes(maxMinutes))
        {
            // Force-end the row so the active-list view immediately
            // reflects reality. No event emission here — the cleanup
            // pass / endpoint layer owns the IMPERSONATION.ENDED event;
            // we just trip the boolean in the audit table.
            await impersonationService
                .EndImpersonationAsync(impId, "session_expired", context.RequestAborted)
                .ConfigureAwait(false);
            logger.LogWarning(
                "impersonation.middleware.session_expired impId={ImpId} startedAt={StartedAt}",
                impId, row.StartedAt);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "impersonation_session_expired",
                message = "The impersonation session has reached its maximum lifetime; re-authenticate to continue.",
            });
            return;
        }

        // All gates passed. Stash the impersonation id for downstream
        // observability + tag the response header.
        context.Items[ImpersonationIdItem] = impId;
        if (!context.Response.HasStarted)
        {
            context.Response.Headers[ImpersonationHeader] = impId.ToString("D");
        }

        await _next(context);
    }

    /// <summary>
    /// Convenience accessor — returns the verified impersonation id stashed
    /// in <see cref="HttpContext.Items"/> by the middleware, or
    /// <c>null</c> if the request is not inside an impersonation session.
    /// Audit-event constructors + log enrichers call this to attach the
    /// breadcrumb without re-parsing the JWT.
    /// </summary>
    public static Guid? GetImpersonationId(HttpContext? context)
    {
        if (context?.Items.TryGetValue(ImpersonationIdItem, out var raw) == true
            && raw is Guid id)
        {
            return id;
        }
        return null;
    }
}
