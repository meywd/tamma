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
/// reports progress.</para>
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
