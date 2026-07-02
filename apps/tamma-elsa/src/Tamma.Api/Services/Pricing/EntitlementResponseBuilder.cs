using Microsoft.Extensions.Logging;
using Tamma.Api.Dtos.Pricing;
using Tamma.Core.Enums;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-6 — composes the wire response for both entitlement read endpoints:
/// resolve the closed map, then per-metric live usage + shared headroom calc.
/// Shared so the member self-read and the admin cross-tenant read produce an
/// identical body shape (AC4/AC5).
///
/// <para>Per-metric usage read is best-effort: a reader that throws for one
/// metric degrades that line to <c>CurrentUsage = null</c> (WARN-logged) — the
/// rest of the set still resolves (edge case in the story).</para>
/// </summary>
public static class EntitlementResponseBuilder
{
    public static async Task<ResolvedEntitlementsDto> BuildAsync(
        IEntitlementService service,
        IEntitlementUsageReader usageReader,
        EntitlementPrincipal principal,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(usageReader);
        ArgumentNullException.ThrowIfNull(logger);

        var resolved = await service.ResolveAsync(principal, ct);

        var lines = new List<ResolvedEntitlementDto>(resolved.Limits.Count);
        foreach (var metric in EntitlementDefaults.AllMetrics)
        {
            var line = resolved.Get(metric);

            long? usage = null;
            try
            {
                // Pass BOTH the resolved tenant (SaaS + single-user personal
                // tenant scope) AND the single-user principal's user id, so
                // USER-owned counts (Agents) resolve in single-user mode where
                // the owner is a user, not the personal tenant.
                usage = await usageReader.GetCurrentAsync(
                    resolved.TenantId, principal.UserId, metric, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Usage reader threw for tenant {TenantId} metric {Metric}; degrading to unavailable",
                    resolved.TenantId, metric.ToMetricString());
            }

            var headroom = EntitlementDefaults.ComputeHeadroom(metric, line.LimitValue, usage);

            lines.Add(new ResolvedEntitlementDto(
                MetricKey: metric.ToMetricString(),
                LimitValue: line.LimitValue,
                Period: line.Period,
                OverageMode: line.OverageMode,
                CurrentUsage: headroom.CurrentUsage,
                Remaining: headroom.Remaining,
                IsOver: headroom.IsOver,
                OveragePercent: headroom.OveragePercent));
        }

        return new ResolvedEntitlementsDto(
            TenantId: resolved.TenantId.ToString(),
            PlanId: resolved.PlanId.ToString(),
            PlanVersion: resolved.PlanVersion,
            IsCustom: resolved.IsCustom,
            Limits: lines);
    }
}
