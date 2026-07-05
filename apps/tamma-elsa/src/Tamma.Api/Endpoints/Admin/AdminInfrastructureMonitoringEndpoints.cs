using Tamma.Api.Services;

namespace Tamma.Api.Endpoints.Admin;

/// <summary>
/// Story 23-8 — the platform-owner INFRASTRUCTURE MONITOR read surface under
/// <c>GET /api/admin/monitoring/infrastructure</c>. One read-only, live snapshot
/// of the API process + host (runtime / CPU / memory / disk / uptime) composed
/// with the coarse up/down status of every backing dependency (Postgres,
/// RabbitMQ, ELSA engine, ChromaDB, OpenSearch).
///
/// <para><b>RBAC / leak defence:</b> infra metrics are SYSTEM/PLATFORM-level, not
/// tenant-scoped, so the route is gated <c>PlatformOwnerAccess</c> at the wiring
/// site (Finding C1) — a regular member / tenant-owner who is not a platform admin
/// gets 403 and never sees process internals. The response carries ONLY system
/// statistics + boolean-ish dependency status; it exposes NO connection string,
/// DB host / user / password, secret, or tenant/customer data (the dependency
/// <c>Detail</c> is allowlist-sanitized in
/// <see cref="InfrastructureMetricsService.SanitizeDetail"/>).</para>
///
/// <para><b>No new logic, no migration:</b> a pure live read of what .NET / the
/// container already expose (<c>GC</c>, <c>Process</c>, <c>DriveInfo</c>, the
/// cgroup filesystem) plus the existing <see cref="IAdminHealthService"/> probe
/// fan-out. No DB writes, no schema, no external metrics stack (Prometheus /
/// node-exporter) is stood up.</para>
/// </summary>
public static class AdminInfrastructureMonitoringEndpoints
{
    // ── GET /api/admin/monitoring/infrastructure ──
    public static async Task<IResult> GetInfrastructure(
        IInfrastructureMetricsService metrics,
        CancellationToken ct)
    {
        var snapshot = await metrics.GetMetricsAsync(ct);
        return Results.Ok(snapshot);
    }
}
