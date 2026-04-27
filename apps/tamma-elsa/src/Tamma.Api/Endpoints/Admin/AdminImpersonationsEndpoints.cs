using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Middleware;
using Tamma.Api.Services.Auth;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Endpoints.Admin;

/// <summary>
/// Story 28-R2 follow-up B — platform-admin impersonation surface.
///
/// <list type="bullet">
///   <item><description><c>POST /api/admin/tenants/{tenantId}/impersonate</c>
///     — begin a session. Body: <c>{ targetUserId?, reason }</c>. Gated by
///     <c>PlatformOwnerAccess</c>. Emits <c>IMPERSONATION.STARTED</c>.</description></item>
///   <item><description><c>POST /api/auth/impersonate/end</c> — end the
///     current session (reads <c>imp_id</c> from the JWT). Emits
///     <c>IMPERSONATION.ENDED</c>. Gated by <c>AuthenticatedAny</c> —
///     any caller holding the impersonation token can end it.</description></item>
///   <item><description><c>GET /api/admin/impersonations/active</c> — list
///     every currently active session across the platform. Gated by
///     <c>PlatformOwnerAccess</c>; the incident-response surface for
///     "who's impersonating right now?".</description></item>
/// </list>
///
/// <para><b>Audit-event shape:</b> both <c>IMPERSONATION.STARTED</c> and
/// <c>IMPERSONATION.ENDED</c> carry the impersonator + target identity in
/// BOTH <c>tags</c> (queryable JSONB) and <c>data</c> (immutable canonical
/// record). This mirrors the M2 actor-event pattern used by
/// <see cref="AdminTenantsEndpoints"/>: tags can be projected/dropped,
/// data is the SOC2 evidence. The matching <c>admin_impersonations</c>
/// row id is stamped into both channels under <c>impersonationId</c> so
/// log queries can join the event stream to the audit table.</para>
/// </summary>
public static class AdminImpersonationsEndpoints
{
    /// <summary>
    /// Request body for <c>POST /api/admin/tenants/{tenantId}/impersonate</c>.
    /// </summary>
    /// <param name="TargetUserId">Optional specific tenant member to
    /// impersonate. <c>null</c> means full-tenant impersonation.</param>
    /// <param name="Reason">Required, charset-whitelisted operator note.
    /// SOC2 evidence — every session must carry a human-readable
    /// justification.</param>
    public sealed record BeginImpersonationRequest(Guid? TargetUserId, string Reason);

    /// <summary>
    /// Response body for <c>POST /tenants/{tenantId}/impersonate</c>.
    /// </summary>
    public sealed record BeginImpersonationResponse(
        Guid ImpersonationId,
        Guid TargetTenantId,
        Guid? TargetUserId,
        string AccessToken,
        DateTime ExpiresAt,
        DateTime MaxSessionExpiresAt);

    /// <summary>
    /// Response item for the <c>GET /impersonations/active</c> list.
    /// Mirrors <see cref="AdminImpersonation"/> minus columns that don't
    /// belong on the wire (e.g. raw User-Agent isn't useful in a list view;
    /// it's available via the row-level audit query).
    /// </summary>
    public sealed record ActiveImpersonationItem(
        Guid Id,
        Guid ImpersonatorUserId,
        string ImpersonatorEmail,
        Guid TargetTenantId,
        Guid? TargetUserId,
        string Reason,
        DateTime StartedAt,
        string? IpAddress);

    public sealed record ActiveImpersonationListResponse(
        IReadOnlyList<ActiveImpersonationItem> Items,
        int Count);

    // ── POST /api/admin/tenants/{tenantId}/impersonate ──

    /// <summary>
    /// Begin an impersonation session. Validates the reason against the
    /// charset whitelist (M17 pattern), inserts a row in
    /// <c>admin_impersonations</c>, mints a tenant-scoped JWT carrying
    /// <c>imp_id</c>, and emits <c>IMPERSONATION.STARTED</c>.
    /// </summary>
    public static async Task<IResult> BeginImpersonation(
        Guid tenantId,
        [FromBody] BeginImpersonationRequest? body,
        HttpContext http,
        IAdminImpersonationService impersonationService,
        IPlatformEventPublisher publisher,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Reason))
        {
            return Results.BadRequest(new
            {
                error = "reason_required",
                message = "reason is required and must match [A-Za-z0-9 .,;:_!@#$%&()-]{1,500}.",
            });
        }

        var ipAddress = http.Connection.RemoteIpAddress?.ToString();
        var userAgent = http.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrEmpty(userAgent)) userAgent = null;

        BeginImpersonationResult result;
        try
        {
            result = await impersonationService.BeginImpersonationAsync(
                principal,
                tenantId,
                body.TargetUserId,
                body.Reason,
                ipAddress,
                userAgent,
                ct);
        }
        catch (ArgumentException ex)
        {
            // Service surfaces validation errors via ArgumentException —
            // map to 400 with the canonical error code.
            var code = ex.ParamName switch
            {
                "reason" => "invalid_reason",
                "targetTenantId" => "tenant_not_found",
                "targetUserId" => "target_user_not_member_of_tenant",
                _ => "invalid_request",
            };
            var statusCode = code == "tenant_not_found"
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status400BadRequest;
            return Results.Json(
                new { error = code, message = ex.Message },
                statusCode: statusCode);
        }

        await publisher.AppendAndPublishAsync(
            BuildImpersonationEvent(
                "IMPERSONATION.STARTED",
                tenantId,
                principal,
                result.ImpersonationId,
                new Dictionary<string, object?>
                {
                    ["targetTenantId"] = tenantId.ToString("D"),
                    ["targetUserId"] = body.TargetUserId?.ToString("D"),
                    ["reason"] = body.Reason.Trim(),
                    ["startedAt"] = DateTime.UtcNow,
                    ["expiresAt"] = result.ExpiresAt,
                    ["maxSessionExpiresAt"] = result.MaxSessionExpiresAt,
                    ["ipAddress"] = ipAddress,
                }),
            ct);

        return Results.Ok(new BeginImpersonationResponse(
            ImpersonationId: result.ImpersonationId,
            TargetTenantId: tenantId,
            TargetUserId: body.TargetUserId,
            AccessToken: result.AccessToken,
            ExpiresAt: result.ExpiresAt,
            MaxSessionExpiresAt: result.MaxSessionExpiresAt));
    }

    // ── POST /api/auth/impersonate/end ──

    /// <summary>
    /// End the current impersonation session. Reads <c>imp_id</c> from the
    /// JWT (verified by the middleware) and stamps <c>EndedAt</c> /
    /// <c>EndedReason</c>. Emits <c>IMPERSONATION.ENDED</c>.
    ///
    /// <para>This endpoint accepts the impersonation JWT (i.e. the token
    /// minted by <see cref="BeginImpersonation"/>) — it's intentionally
    /// NOT gated by <c>PlatformOwnerAccess</c> because the operator's
    /// impersonation token has <c>platformRole = user</c> from the
    /// target's perspective. Authentication is enough; the <c>imp_id</c>
    /// claim itself is the proof-of-possession that authorises the
    /// end-call.</para>
    /// </summary>
    public static async Task<IResult> EndImpersonation(
        IAdminImpersonationService impersonationService,
        IPlatformEventPublisher publisher,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct = default)
    {
        var claim = principal.FindFirst(ImpersonationContextMiddleware.ImpersonationClaim)?.Value;
        if (string.IsNullOrEmpty(claim) || !Guid.TryParse(claim, out var impId))
        {
            return Results.Json(
                new
                {
                    error = "no_active_impersonation",
                    message = "Current request does not carry an active imp_id claim.",
                },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var row = await impersonationService.EndImpersonationAsync(
            impId, "explicit_exit", ct);
        if (row is null)
        {
            return Results.Json(
                new
                {
                    error = "impersonation_already_ended",
                    message = "Session has already been ended.",
                },
                statusCode: StatusCodes.Status410Gone);
        }

        await publisher.AppendAndPublishAsync(
            BuildImpersonationEvent(
                "IMPERSONATION.ENDED",
                row.TargetTenantId,
                principal,
                row.Id,
                new Dictionary<string, object?>
                {
                    ["targetTenantId"] = row.TargetTenantId.ToString("D"),
                    ["targetUserId"] = row.TargetUserId?.ToString("D"),
                    ["startedAt"] = row.StartedAt,
                    ["endedAt"] = row.EndedAt,
                    ["endedReason"] = row.EndedReason,
                    ["durationSeconds"] = row.EndedAt.HasValue
                        ? (long)(row.EndedAt.Value - row.StartedAt).TotalSeconds
                        : (long?)null,
                }),
            ct);

        return Results.Ok(new
        {
            impersonationId = row.Id,
            endedAt = row.EndedAt,
            endedReason = row.EndedReason,
        });
    }

    // ── GET /api/admin/impersonations/active ──

    /// <summary>
    /// List every currently active impersonation session. Hits the
    /// <c>idx_admin_impersonations_active</c> partial index. The result
    /// is the incident-response payload for "who's impersonating right
    /// now?".
    /// </summary>
    public static async Task<IResult> ListActive(
        IAdminImpersonationService impersonationService,
        CancellationToken ct = default)
    {
        var rows = await impersonationService.ListAllActiveAsync(ct);
        var items = rows.Select(r => new ActiveImpersonationItem(
            r.Id,
            r.ImpersonatorUserId,
            r.ImpersonatorEmail,
            r.TargetTenantId,
            r.TargetUserId,
            r.Reason,
            r.StartedAt,
            r.IpAddress)).ToList();
        return Results.Ok(new ActiveImpersonationListResponse(items, items.Count));
    }

    // ── helpers ──

    /// <summary>
    /// Build the <c>IMPERSONATION.*</c> platform event with the operator
    /// identity AND the impersonation-row id in BOTH <c>tags</c> and
    /// <c>data</c> channels. Mirrors the M2 pattern used by
    /// <see cref="AdminTenantsEndpoints"/> for the audit-event shape.
    /// </summary>
    private static PlatformEvent BuildImpersonationEvent(
        string type,
        Guid tenantId,
        ClaimsPrincipal? principal,
        Guid impersonationId,
        IReadOnlyDictionary<string, object?>? data)
    {
        var (userId, email, platformRole) = ExtractActor(principal);

        var tags = new Dictionary<string, string?>
        {
            ["tenantId"] = tenantId.ToString("D"),
            ["source"] = "admin",
            ["impersonationId"] = impersonationId.ToString("D"),
        };
        if (!string.IsNullOrEmpty(userId)) tags["actorUserId"] = userId;
        if (!string.IsNullOrEmpty(email)) tags["actorEmail"] = email;
        if (!string.IsNullOrEmpty(platformRole)) tags["actorPlatformRole"] = platformRole;

        var enriched = data is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(data);
        enriched["actorUserId"] = userId;
        enriched["actorEmail"] = email;
        enriched["actorPlatformRole"] = platformRole;
        enriched["impersonationId"] = impersonationId.ToString("D");

        return new PlatformEvent
        {
            Type = type,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""",
            Data = JsonSerializer.Serialize(enriched),
        };
    }

    /// <summary>
    /// Extract <c>(userId, email, platformRole)</c> from the principal.
    /// Returns nulls when the principal is missing claims (permissive-dev
    /// tests) — the audit row writes whatever's available.
    /// </summary>
    private static (string? UserId, string? Email, string? PlatformRole) ExtractActor(
        ClaimsPrincipal? principal)
    {
        if (principal is null) return (null, null, null);

        var userId = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.FindFirst("email")?.Value;
        var platformRole = principal.FindFirst("platformRole")?.Value;

        return (userId, email, platformRole);
    }
}
