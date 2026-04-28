using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Services.Analytics;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 28-10 — platform-admin analytics endpoints mounted under
/// <c>/api/admin/analytics/*</c>. Every endpoint in this file MUST be
/// gated behind the <c>OwnerAccess</c> policy at the wiring site because
/// the service reads across every tenant regardless of the caller's
/// <c>TenantId</c>.
///
/// <para>Three endpoints:
/// <list type="bullet">
///   <item><description><c>GET /api/admin/analytics/summary</c> — one-shot
///     dashboard summary (tenant counts + workflow / agent-dispatch / cost
///     counters over 24h / 7d / 30d).</description></item>
///   <item><description><c>GET /api/admin/analytics/tenants?limit=N</c> —
///     top-N tenants by 30-day workflow volume.</description></item>
///   <item><description><c>GET /api/admin/analytics/events?since=ISO&amp;limit=N</c>
///     — event-type histogram over <c>platform_events</c>.</description></item>
/// </list></para>
/// </summary>
public static class AdminAnalyticsEndpoints
{
    /// <summary>
    /// Returns the cross-tenant analytics summary used by the admin
    /// dashboard's default view.
    /// </summary>
    public static async Task<IResult> GetSummary(
        IPlatformAnalyticsService analytics,
        CancellationToken ct)
    {
        var summary = await analytics.GetSummaryAsync(ct);
        return Results.Ok(summary);
    }

    /// <summary>
    /// Returns the top-N tenants ordered by 30-day workflow volume.
    /// <paramref name="limit"/> is clamped to 1..200.
    /// </summary>
    public static async Task<IResult> GetTopTenants(
        [FromQuery] int? limit,
        IPlatformAnalyticsService analytics,
        CancellationToken ct)
    {
        var rows = await analytics.GetTopTenantsAsync(limit ?? 25, ct);
        return Results.Ok(new { tenants = rows });
    }

    /// <summary>
    /// Returns the event-type histogram for <c>[since, now)</c>. When
    /// <paramref name="since"/> is absent, defaults to 24 hours ago.
    /// </summary>
    public static async Task<IResult> GetEventHistogram(
        [FromQuery] DateTime? since,
        [FromQuery] int? limit,
        IPlatformAnalyticsService analytics,
        CancellationToken ct)
    {
        // DateTime.Kind round-tripping: query binding parses as Local, but
        // the service expects UTC. Force UTC so the query window matches
        // the stored CreatedAt column (which is UTC per the CP schema).
        var sinceUtc = since.HasValue
            ? DateTime.SpecifyKind(since.Value.ToUniversalTime(), DateTimeKind.Utc)
            : (DateTime?)null;

        var buckets = await analytics.GetEventHistogramAsync(sinceUtc, limit ?? 20, ct);
        return Results.Ok(new { buckets });
    }
}
