using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Services.Analytics;
using Tamma.Api.Services.PromptStore;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 36-3 — tenant-facing usage analytics endpoints mounted under
/// <c>/api/v1/orgs/{tenantId}/analytics/*</c>. The tenant-facing mirror of the
/// platform-owner <see cref="AdminAnalyticsEndpoints"/>: it reads the
/// <b>per-tenant</b> Story 36-1 fact tables (<c>analytics_usage_hourly</c> /
/// <c>analytics_usage_daily</c>) through <c>ITenantAnalyticsService</c>, never
/// the control-plane <c>platform_analytics_hourly</c> / <c>IPlatformAnalyticsService</c>.
///
/// <para><b>Auth (AC4/AC5/AC6):</b> both routes carry the group's
/// <c>MemberAccess</c> policy + the
/// <see cref="Tamma.Api.Authorization.RequireTenantMembershipFilter"/> (403 for
/// a non-member of the route <c>{tenantId}</c>). <b>No</b> owner/admin gate —
/// usage analytics is member-readable. The endpoint shape is identical across
/// single-user / SaaS; the membership filter + the per-tenant
/// <c>TenantDbContext</c> do all the mode + isolation work, so the handlers
/// have no per-mode branch.</para>
///
/// <para><b>Thin by design (AC1/AC3/AC10):</b> the handlers parse + clamp the
/// window (forced-UTC, 365-day max, hour cap), 400 on bad input, and delegate
/// the grouped/aggregated read to the service.</para>
/// </summary>
public static class TenantAnalyticsEndpoints
{
    private const int DefaultBreakdownLimit = 10;
    private const int MaxBreakdownLimit = 100;

    /// <summary>
    /// <c>GET /api/v1/orgs/{tenantId}/analytics/usage</c> — time-bucketed usage
    /// over <c>[from, to)</c> at <c>hour</c>|<c>day</c> grain, optionally grouped
    /// by <c>provider</c>|<c>agent</c>|<c>workflow</c>|<c>repo</c>.
    /// </summary>
    public static async Task<IResult> GetUsage(
        Guid tenantId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? granularity,
        [FromQuery] string? groupBy,
        ITenantAnalyticsService analytics,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Tamma.Api.Endpoints.TenantAnalyticsEndpoints");

        var window = AnalyticsWindow.Resolve(from, to, granularity);
        if (!window.IsValid)
        {
            log.LogWarning(
                "Tenant usage query rejected: tenantId={TenantId} requestedFrom={From} "
                    + "requestedTo={To} granularity={Granularity} error={Error}",
                tenantId, from, to, granularity, window.Error);
            return Results.BadRequest(new { error = window.Error });
        }

        AnalyticsDimension? groupByDim = null;
        if (!string.IsNullOrWhiteSpace(groupBy))
        {
            if (!AnalyticsEnums.TryParseDimension(groupBy, out var dim))
            {
                return Results.BadRequest(new
                {
                    error = "groupBy must be one of: provider, agent, workflow, repo",
                });
            }

            groupByDim = dim;
        }

        LogWindowDebug(log, tenantId, from, to, window);

        var result = await analytics.GetUsageAsync(tenantId, window, groupByDim, ct);
        return Results.Ok(result);
    }

    /// <summary>
    /// <c>GET /api/v1/orgs/{tenantId}/analytics/usage/breakdown</c> — top-N rows
    /// for a single <c>dimension</c> over the window, ranked by <c>metric</c>
    /// (<c>tokens</c>|<c>runs</c>|<c>dispatches</c>|<c>cost</c>) descending.
    /// Always daily grain (the natural breakdown source).
    /// </summary>
    public static async Task<IResult> GetBreakdown(
        Guid tenantId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? dimension,
        [FromQuery] string? metric,
        [FromQuery] int? limit,
        ITenantAnalyticsService analytics,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Tamma.Api.Endpoints.TenantAnalyticsEndpoints");

        // Breakdown ranks over the whole window at daily grain — resolve with the
        // default (day) granularity so the 365-day max range still applies.
        var window = AnalyticsWindow.Resolve(from, to, null);
        if (!window.IsValid)
        {
            log.LogWarning(
                "Tenant breakdown query rejected: tenantId={TenantId} requestedFrom={From} "
                    + "requestedTo={To} error={Error}",
                tenantId, from, to, window.Error);
            return Results.BadRequest(new { error = window.Error });
        }

        if (!AnalyticsEnums.TryParseDimension(dimension, out var dim))
        {
            return Results.BadRequest(new
            {
                error = "dimension is required and must be one of: provider, agent, workflow, repo",
            });
        }

        if (!AnalyticsEnums.TryParseMetric(metric, out var met))
        {
            return Results.BadRequest(new
            {
                error = "metric must be one of: tokens, runs, dispatches, cost",
            });
        }

        var clampedLimit = Math.Clamp(limit ?? DefaultBreakdownLimit, 1, MaxBreakdownLimit);

        LogWindowDebug(log, tenantId, from, to, window);

        var result = await analytics.GetBreakdownAsync(tenantId, window, dim, met, clampedLimit, ct);
        return Results.Ok(result);
    }

    /// <summary>
    /// Story 36-4 — <c>GET /api/v1/orgs/{tenantId}/analytics/cost</c>. A daily
    /// BYOK/platform cost split over <c>[from, to)</c> (optionally grouped by
    /// <c>provider</c>|<c>agent</c>), plus month-to-date + linear-run-rate
    /// projected platform-billed spend, a <c>BudgetConfig</c> join, and a trend
    /// vs the prior equivalent window.
    ///
    /// <para>Reuses the Story 36-3 <see cref="AnalyticsWindow"/> for forced-UTC
    /// binding + range clamp; the only difference from <see cref="GetUsage"/> is
    /// (a) the window <b>defaults to the current calendar month</b> (UTC) when
    /// omitted — the natural budget/MTD frame — and (b) <c>groupBy</c> accepts
    /// only <c>provider</c>|<c>agent</c> (cost has no workflow/repo split).</para>
    /// </summary>
    public static async Task<IResult> GetCost(
        Guid tenantId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? groupBy,
        ICostAnalyticsService cost,
        ITammaModeProvider modeProvider,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Tamma.Api.Endpoints.TenantAnalyticsEndpoints");

        // groupBy is optional; cost only splits by provider|agent (not workflow/repo).
        AnalyticsDimension? groupByDim = null;
        if (!string.IsNullOrWhiteSpace(groupBy))
        {
            switch (groupBy.Trim().ToLowerInvariant())
            {
                case "provider":
                    groupByDim = AnalyticsDimension.Provider;
                    break;
                case "agent":
                    groupByDim = AnalyticsDimension.Agent;
                    break;
                default:
                    return Results.BadRequest(new { error = "groupBy must be one of: provider, agent" });
            }
        }

        // Default the window to the current calendar month (UTC) when omitted —
        // the frame budget-vs-actual + MTD projection are reasoned about. Then
        // hand off to AnalyticsWindow.Resolve for the shared forced-UTC binding,
        // from<to validation, and 365-day clamp (reused verbatim from 36-3).
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var nextMonthStart = monthStart.AddMonths(1);
        var effectiveFrom = from ?? monthStart;
        var effectiveTo = to ?? nextMonthStart;

        var window = AnalyticsWindow.Resolve(effectiveFrom, effectiveTo, "day");
        if (!window.IsValid)
        {
            log.LogWarning(
                "Tenant cost query rejected: tenantId={TenantId} requestedFrom={From} "
                    + "requestedTo={To} error={Error}",
                tenantId, from, to, window.Error);
            return Results.BadRequest(new { error = window.Error });
        }

        LogWindowDebug(log, tenantId, from, to, window);

        var mode = modeProvider.Mode == TammaMode.SaaS ? "saas" : "single-user";
        var result = await cost.GetCostAsync(tenantId, window, groupByDim, mode, ct);
        return Results.Ok(result);
    }

    private static void LogWindowDebug(
        ILogger log, Guid tenantId, DateTime? requestedFrom, DateTime? requestedTo, AnalyticsWindow window)
    {
        if (!log.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        log.LogDebug(
            "Tenant analytics window resolved: tenantId={TenantId} requestedFrom={RequestedFrom} "
                + "requestedTo={RequestedTo} effectiveFrom={EffectiveFrom:o} effectiveTo={EffectiveTo:o} "
                + "granularity={Granularity}",
            tenantId, requestedFrom, requestedTo, window.From, window.To, window.Granularity.ToWire());
    }
}
