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
    /// </summary>
    public static IResult Start(KekRotationCoordinator coordinator, HttpContext http)
    {
        var status = coordinator.StartAsync(newKek: null, cancellationToken: http.RequestAborted);
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
