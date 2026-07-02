using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Services.Analytics;

/// <summary>
/// Story 36-3 — per-tenant aggregation over the Story 36-1 dimensional fact
/// tables. Mirrors the <c>UserDashboardEndpoints.GetStats</c> read shape:
/// <c>await using var db = await tenantDbFactory.CreateAsync(tenantId)</c>, then
/// a LINQ read against the tenant schema. Reads the pre-aggregated facts ONLY.
///
/// <para><b>Why materialize-then-group:</b> the two grains live on separate
/// DbSets with different bucket columns (<c>Hour</c>/<c>Day</c>). The query
/// pushes the indexed half-open <c>[from, to)</c> bucket-range filter to
/// Postgres (using <c>IX_analytics_usage_*_breakdown</c>), projects only the
/// needed columns, and rolls the (bounded — 365 daily buckets, or ≤31 days
/// hourly per the AC7 caps) result up in memory. In-memory rollup gives
/// byte-identical grouping on InMemory and Npgsql — including the <c>NULL</c>
/// dimension key, which EF providers translate inconsistently — so the 36-2
/// reconciliation contract (Σ grouped == ungrouped) holds under both.</para>
/// </summary>
public sealed class TenantAnalyticsService(
    ITenantDbContextFactory tenantDbFactory,
    ILogger<TenantAnalyticsService> logger)
    : ITenantAnalyticsService
{
    public async Task<UsageResponse> GetUsageAsync(
        Guid tenantId,
        AnalyticsWindow window,
        AnalyticsDimension? groupBy,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var facts = await LoadFactsAsync(tenantId, window, ct);

        List<UsageBucketRow> rows;
        if (groupBy is null)
        {
            rows = facts
                .GroupBy(f => f.Bucket)
                .OrderBy(g => g.Key)
                .Select(g => AggregateBucket(g.Key, null, g))
                .ToList();
        }
        else
        {
            var keyOf = DimensionKeySelector(groupBy.Value);
            rows = facts
                .GroupBy(f => new BucketKey(f.Bucket, keyOf(f)))
                .OrderBy(g => g.Key.Bucket)
                .ThenBy(g => g.Key.Key, StringComparer.Ordinal)
                .Select(g => AggregateBucket(g.Key.Bucket, g.Key.Key, g))
                .ToList();
        }

        logger.LogInformation(
            "Tenant usage analytics served: tenantId={TenantId} granularity={Granularity} "
                + "groupBy={GroupBy} periodStart={PeriodStart:o} periodEnd={PeriodEnd:o} "
                + "rowCount={RowCount} durationMs={DurationMs}",
            tenantId, window.Granularity.ToWire(), groupBy?.ToWire() ?? "(none)",
            window.From, window.To, rows.Count, sw.ElapsedMilliseconds);

        if (rows.Count == 0)
        {
            logger.LogWarning(
                "Tenant usage analytics returned no rows for a non-trivial window "
                    + "(tenantId={TenantId}, periodStart={PeriodStart:o}, periodEnd={PeriodEnd:o}) "
                    + "— tenant may be un-projected; check the 36-2 rollup lag SLO.",
                tenantId, window.From, window.To);
        }

        return new UsageResponse(
            tenantId,
            window.From,
            window.To,
            window.Granularity.ToWire(),
            groupBy?.ToWire(),
            rows);
    }

    public async Task<BreakdownResponse> GetBreakdownAsync(
        Guid tenantId,
        AnalyticsWindow window,
        AnalyticsDimension dimension,
        AnalyticsMetric metric,
        int limit,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var facts = await LoadFactsAsync(tenantId, window, ct);
        var keyOf = DimensionKeySelector(dimension);

        var rows = facts
            .GroupBy(keyOf)
            .Select(g => AggregateBreakdown(g.Key, g, metric))
            .OrderByDescending(r => r.Value)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        logger.LogInformation(
            "Tenant breakdown analytics served: tenantId={TenantId} dimension={Dimension} "
                + "metric={Metric} limit={Limit} periodStart={PeriodStart:o} periodEnd={PeriodEnd:o} "
                + "rowCount={RowCount} durationMs={DurationMs}",
            tenantId, dimension.ToWire(), metric.ToWire(), limit,
            window.From, window.To, rows.Count, sw.ElapsedMilliseconds);

        return new BreakdownResponse(
            tenantId,
            window.From,
            window.To,
            dimension.ToWire(),
            metric.ToWire(),
            limit,
            rows);
    }

    /// <summary>
    /// Pushes the granularity switch + half-open <c>[from, to)</c> bucket filter
    /// to the tenant schema, projecting only the dimensions + measures needed
    /// for the rollup.
    /// </summary>
    private async Task<List<FactRow>> LoadFactsAsync(
        Guid tenantId, AnalyticsWindow window, CancellationToken ct)
    {
        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);

        if (window.Granularity == AnalyticsGranularity.Day)
        {
            return await db.AnalyticsUsageDaily
                .Where(r => r.Day >= window.From && r.Day < window.To)
                .Select(r => new FactRow(
                    r.Day, r.Provider, r.AgentId, r.WorkflowDefinitionId, r.RepoId,
                    r.TokensIn, r.TokensOut, r.CostUsd, r.PlatformBilledUsd,
                    r.WorkflowsStarted, r.WorkflowsCompleted, r.WorkflowsFailed, r.AgentDispatches))
                .ToListAsync(ct);
        }

        return await db.AnalyticsUsageHourly
            .Where(r => r.Hour >= window.From && r.Hour < window.To)
            .Select(r => new FactRow(
                r.Hour, r.Provider, r.AgentId, r.WorkflowDefinitionId, r.RepoId,
                r.TokensIn, r.TokensOut, r.CostUsd, r.PlatformBilledUsd,
                r.WorkflowsStarted, r.WorkflowsCompleted, r.WorkflowsFailed, r.AgentDispatches))
            .ToListAsync(ct);
    }

    private static Func<FactRow, string?> DimensionKeySelector(AnalyticsDimension dim) => dim switch
    {
        AnalyticsDimension.Provider => f => f.Provider,
        AnalyticsDimension.Agent => f => f.AgentId,
        AnalyticsDimension.Workflow => f => f.WorkflowDefinitionId?.ToString(),
        AnalyticsDimension.Repo => f => f.RepoId,
        _ => f => null,
    };

    private static UsageBucketRow AggregateBucket(
        DateTime bucket, string? key, IEnumerable<FactRow> group)
    {
        var m = Measures.Sum(group);
        return new UsageBucketRow(
            bucket, key,
            m.WorkflowsStarted, m.WorkflowsCompleted, m.WorkflowsFailed, m.AgentDispatches,
            m.TokensIn, m.TokensOut, m.CostUsd, m.PlatformBilledUsd);
    }

    private static BreakdownRow AggregateBreakdown(
        string? key, IEnumerable<FactRow> group, AnalyticsMetric metric)
    {
        var m = Measures.Sum(group);
        var value = metric switch
        {
            AnalyticsMetric.Tokens => m.TokensIn + m.TokensOut,
            AnalyticsMetric.Runs => m.WorkflowsStarted,
            AnalyticsMetric.Dispatches => m.AgentDispatches,
            AnalyticsMetric.Cost => m.CostUsd,
            _ => 0m,
        };
        return new BreakdownRow(
            key, value,
            m.TokensIn, m.TokensOut, m.CostUsd, m.PlatformBilledUsd,
            m.WorkflowsStarted, m.WorkflowsCompleted, m.WorkflowsFailed, m.AgentDispatches);
    }

    /// <summary>Lightweight projection of a fact row (only the columns the rollup needs).</summary>
    private readonly record struct FactRow(
        DateTime Bucket,
        string? Provider,
        string? AgentId,
        Guid? WorkflowDefinitionId,
        string? RepoId,
        long TokensIn,
        long TokensOut,
        decimal CostUsd,
        decimal PlatformBilledUsd,
        long WorkflowsStarted,
        long WorkflowsCompleted,
        long WorkflowsFailed,
        long AgentDispatches);

    /// <summary>Grouping key for a grouped usage query — a <c>(bucket, dimension-value)</c> tuple.</summary>
    private readonly record struct BucketKey(DateTime Bucket, string? Key);

    /// <summary>Accumulated measure totals for a group.</summary>
    private readonly record struct Measures(
        long TokensIn,
        long TokensOut,
        decimal CostUsd,
        decimal PlatformBilledUsd,
        long WorkflowsStarted,
        long WorkflowsCompleted,
        long WorkflowsFailed,
        long AgentDispatches)
    {
        public static Measures Sum(IEnumerable<FactRow> group)
        {
            long tin = 0, tout = 0, ws = 0, wc = 0, wf = 0, ad = 0;
            decimal cu = 0m, pb = 0m;
            foreach (var f in group)
            {
                tin += f.TokensIn;
                tout += f.TokensOut;
                cu += f.CostUsd;
                pb += f.PlatformBilledUsd;
                ws += f.WorkflowsStarted;
                wc += f.WorkflowsCompleted;
                wf += f.WorkflowsFailed;
                ad += f.AgentDispatches;
            }

            return new Measures(tin, tout, cu, pb, ws, wc, wf, ad);
        }
    }
}
