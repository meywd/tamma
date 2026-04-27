using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 28-12 — admin-facing endpoints that drive the KEK rotation
/// flow. The endpoints are gated by the <c>OwnerAccess</c> policy at
/// the route mapping site (Program.cs) — this matches the existing
/// platform-owner surface used by tenant provisioning.
///
/// <para><c>POST /api/admin/kek/rotate/start</c> kicks off a rotation
/// (returns 202 with the snapshot). <c>GET /api/admin/kek/rotate/status</c>
/// reports progress. R2-H3 adds <c>POST /api/admin/kek/rotate/retry</c>
/// — re-attempts a previously-failed rotation by re-using the staged
/// secondary that's still on disk (idempotent re-run; does NOT mint a
/// new KEK because that would orphan rows already re-encrypted under
/// the failed run's secondary).</para>
/// </summary>
public static class KekRotationEndpoints
{
    /// <summary>
    /// Trigger a rotation. The coordinator generates a new 32-byte
    /// KEK, stages it as the secondary, then runs the per-tenant
    /// re-encrypt loop on a background task. Subsequent calls while a
    /// rotation is in flight return the running snapshot rather than
    /// stacking a second rotation.
    ///
    /// <para>Story 28-R2 / Finding M2 — captures the operator identity
    /// from the JWT and threads it into the coordinator so the
    /// <c>SECRETS.KEK.ROTATION.STARTED/COMPLETED/FAILED</c> events
    /// record who kicked the rotation off.</para>
    /// </summary>
    public static IResult Start(
        KekRotationCoordinator coordinator,
        ClaimsPrincipal principal,
        HttpContext http)
    {
        var actorUserId = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var actorEmail = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.FindFirst("email")?.Value;
        var actorPlatformRole = principal.FindFirst("platformRole")?.Value;

        var status = coordinator.StartAsync(
            newKek: null,
            cancellationToken: http.RequestAborted,
            actorUserId: actorUserId,
            actorEmail: actorEmail,
            actorPlatformRole: actorPlatformRole);
        return Results.Accepted(uri: "/api/admin/kek/rotate/status", value: ToResponse(status));
    }

    /// <summary>
    /// Report the current rotation phase + counters. Cheap; safe to
    /// poll on a short interval from the runbook UI.
    /// </summary>
    public static IResult GetStatus(KekRotationCoordinator coordinator)
    {
        var status = coordinator.GetStatus();
        return Results.Ok(ToResponse(status));
    }

    /// <summary>
    /// R2-H3 — retry the last failed rotation. Returns 202 on success,
    /// 409 when the current phase is not <see cref="KekRotationPhase.Failed"/>
    /// (e.g. the rotation is currently Running, or the previous one
    /// completed cleanly). The retry re-uses the staged secondary
    /// persisted in <c>kek_rotations</c> by the failed run; it does
    /// NOT mint a fresh KEK.
    ///
    /// <para>R2 post-fix (retry-actor-identity): the operator's
    /// <see cref="ClaimsPrincipal"/> is now threaded into the
    /// coordinator so retry-emitted events
    /// (<c>SECRETS.KEK.ROTATION.STARTED/COMPLETED/FAILED</c>) carry
    /// the caller's <c>sub</c>/<c>email</c>/<c>platformRole</c>
    /// claims — the failed run's original actor lives on its own
    /// STARTED row, and the retry's actor lives on its retry STARTED
    /// row. This keeps the audit trail accurate when a different
    /// operator retries someone else's failed rotation.</para>
    /// </summary>
    public static async Task<IResult> Retry(
        KekRotationCoordinator coordinator,
        ClaimsPrincipal principal,
        HttpContext http)
    {
        var response = await coordinator.RetryAsync(principal, http.RequestAborted);
        if (!response.Success)
        {
            return Results.Conflict(new
            {
                reason = response.Reason,
                status = ToResponse(response.Status),
            });
        }
        return Results.Accepted(
            uri: "/api/admin/kek/rotate/status",
            value: ToResponse(response.Status));
    }

    private static object ToResponse(KekRotationStatus status) => new
    {
        phase = status.Phase.ToString().ToLowerInvariant(),
        fromVersion = status.FromVersion,
        toVersion = status.ToVersion,
        totalTenants = status.TotalTenants,
        reencryptedTenants = status.ReencryptedTenants,
        failedTenants = status.FailedTenants,
        startedAt = status.StartedAt,
        completedAt = status.CompletedAt,
        failureReason = status.FailureReason,
    };
}
