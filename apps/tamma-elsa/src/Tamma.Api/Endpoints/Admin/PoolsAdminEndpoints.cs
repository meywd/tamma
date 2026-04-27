using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;

namespace Tamma.Api.Endpoints.Admin;

/// <summary>
/// Story 28-4 AC5 — admin diagnostics + manual control over the
/// per-tenant connection pool. Three endpoints:
/// <list type="bullet">
///   <item><description><c>GET /api/admin/pools/stats</c> —
///     process-wide pool counters (warm count, cache hit ratio,
///     evictions broken down by reason).</description></item>
///   <item><description><c>GET /api/admin/pools/tenants?limit=N</c> —
///     list of currently-warm tenants in MRU order with outstanding
///     lease counts (per-tenant view; helps spot a long-running SSE
///     stream blocking eviction).</description></item>
///   <item><description><c>POST /api/admin/pools/{tenantId}/evict</c> —
///     forcibly evict a tenant from the cache (deletes / rotation
///     paths use this; manual eviction is for ops-firefighting).</description></item>
/// </list>
///
/// <para>All three endpoints MUST be gated behind the <c>OwnerAccess</c>
/// policy at the wiring site because they expose cross-tenant
/// infrastructure state. The handlers themselves stay policy-agnostic
/// so unit tests can drive them directly.</para>
///
/// <para>The diagnostics interface (<see cref="IAdminPoolDiagnostics"/>)
/// is implemented only by <see cref="LruPooledTenantConnectionResolver"/>
/// — when the stub is wired (test fixtures + non-pool-cutover
/// composition roots), the DI container does NOT provide a binding and
/// the handlers return <c>503 Service Unavailable</c> with a clear
/// message rather than throwing.</para>
/// </summary>
public static class PoolsAdminEndpoints
{
    /// <summary>
    /// Returns the process-wide connection-pool counters.
    /// </summary>
    public static IResult GetStats(
        [FromServices] IServiceProvider services)
    {
        var diag = services.GetService<IAdminPoolDiagnostics>();
        if (diag is null)
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Tenant connection pool diagnostics unavailable",
                detail: "The current composition root wires the stub " +
                    "tenant-connection resolver. The /api/admin/pools/* " +
                    "endpoints require the production LRU resolver " +
                    "(see TenantConnectionPoolServiceCollectionExtensions).");

        var resolver = services.GetService<ITenantConnectionResolver>();
        var snapshot = resolver?.GetStats();
        return Results.Ok(new
        {
            detailed = diag.GetDetailedStats(),
            snapshot,
        });
    }

    /// <summary>
    /// Lists currently-warm tenants in most-recently-used order, with
    /// outstanding lease counts (0 = safe to evict immediately).
    /// </summary>
    public static IResult ListTenants(
        [FromQuery] int? limit,
        [FromServices] IServiceProvider services)
    {
        var diag = services.GetService<IAdminPoolDiagnostics>();
        if (diag is null)
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Tenant connection pool diagnostics unavailable",
                detail: "Stub resolver wired — see /pools/stats for details.");

        // Default 50, clamped to 1..1000 by the diagnostics impl.
        var entries = diag.ListWarmTenants(limit ?? 50);
        return Results.Ok(new { tenants = entries });
    }

    /// <summary>
    /// Forcibly evicts a tenant from the connection pool. If a lease is
    /// outstanding, the underlying data source is deferred-disposed
    /// when the final lease releases (the cache entry itself is removed
    /// immediately so subsequent requests build a fresh pool).
    ///
    /// <para>Story 28-R2 / Finding M2 — emits a <c>POOL.EVICTED.SUCCESS</c>
    /// platform event capturing the operator identity (sub + email) so
    /// SIEM can correlate manual pool eviction with downstream connection
    /// churn / latency spikes.</para>
    /// </summary>
    public static async Task<IResult> Evict(
        Guid tenantId,
        [FromServices] ITenantConnectionResolver resolver,
        [FromServices] IPlatformEventPublisher eventPublisher,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId required" });

        await resolver.EvictAsync(tenantId, ct);

        // Audit best-effort. Eviction already happened; if the publisher
        // throws (DB outage, downstream failure) we still return 200 so
        // ops doesn't think their evict was rejected.
        try
        {
            await eventPublisher.AppendAndPublishAsync(
                BuildPoolEvictedEvent(tenantId, principal), ct);
        }
        catch
        {
            // Swallow — see comment above.
        }

        return Results.Ok(new { tenantId, status = "evicted" });
    }

    private static PlatformEvent BuildPoolEvictedEvent(
        Guid tenantId, ClaimsPrincipal? principal)
    {
        var actorUserId = principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var actorEmail = principal?.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal?.FindFirst(ClaimTypes.Email)?.Value
            ?? principal?.FindFirst("email")?.Value;
        var actorPlatformRole = principal?.FindFirst("platformRole")?.Value;

        var tags = new Dictionary<string, string?>
        {
            ["tenantId"] = tenantId.ToString("D"),
            ["source"] = "admin-pool-evict",
        };
        if (!string.IsNullOrEmpty(actorUserId)) tags["actorUserId"] = actorUserId;
        if (!string.IsNullOrEmpty(actorEmail)) tags["actorEmail"] = actorEmail;
        if (!string.IsNullOrEmpty(actorPlatformRole)) tags["actorPlatformRole"] = actorPlatformRole;

        var data = new Dictionary<string, object?>
        {
            ["tenantId"] = tenantId.ToString("D"),
            ["actorUserId"] = actorUserId,
            ["actorEmail"] = actorEmail,
            ["actorPlatformRole"] = actorPlatformRole,
            ["evictedAt"] = DateTime.UtcNow,
        };

        return new PlatformEvent
        {
            Type = "POOL.EVICTED.SUCCESS",
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        };
    }
}
