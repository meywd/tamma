using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Analytics;

/// <summary>
/// Story 36-4 — cost/spend read side over the per-tenant Story 36-1
/// <c>analytics_usage_daily</c> fact table. Mirrors
/// <see cref="TenantAnalyticsService"/>'s materialise-then-group shape (open the
/// tenant schema via <c>ITenantDbContextFactory</c>, project only the columns
/// the roll-up needs, aggregate in memory) so grouping — including the
/// <c>NULL</c> dimension key — is byte-identical on EF-InMemory and Npgsql.
///
/// <para><b>Read-only + no re-markup (AC4/AC10):</b> the ctor takes NO markup
/// engine (Story 34-5) or pricing-config dependency. <c>byokCostUsd</c> sums
/// <c>CostUsd</c> of <see cref="CostBasis.Byok"/> rows (informational);
/// <c>platformBilledUsd</c> sums the materialised <c>PlatformBilledUsd</c>
/// column (byok rows are structurally 0 — Story 36-2). The only write is the
/// deduped budget-exceeded DCB event.</para>
/// </summary>
public sealed class CostAnalyticsService(
    ITenantDbContextFactory tenantDbFactory,
    IBudgetConfigRepository budgets,
    IEventRepository events,
    TimeProvider timeProvider,
    ILogger<CostAnalyticsService> logger)
    : ICostAnalyticsService
{
    private const string Unattributed = "unattributed";

    public async Task<CostAnalyticsResponse> GetCostAsync(
        Guid tenantId,
        AnalyticsWindow window,
        AnalyticsDimension? groupBy,
        string mode,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var mtdEndExclusive = now.Date.AddDays(1);   // include today's UTC-midnight bucket
        var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
        var daysElapsed = now.Day;                   // 1..daysInMonth

        // Prior equivalent window — the immediately-preceding window of the same
        // length (for the trend delta).
        var windowLength = window.To - window.From;
        var priorTo = window.From;
        var priorFrom = window.From - windowLength;

        // One tenant context, three bounded half-open [from, to) reads. The
        // per-tenant factory binds a schema-scoped connection, so every read is
        // physically confined to this tenant's schema (no TenantId predicate).
        await using var db = await tenantDbFactory.CreateAsync(tenantId, ct);
        var windowRows = await LoadDailyAsync(db, window.From, window.To, ct);
        var priorRows = await LoadDailyAsync(db, priorFrom, priorTo, ct);
        var mtdRows = await LoadDailyAsync(db, monthStart, mtdEndExclusive, ct);

        // ── Series (BYOK/platform split, optionally grouped) ──
        var series = BuildSeries(windowRows, groupBy);

        // ── Window totals ──
        var windowByok = SumByokCost(windowRows);        // informational
        var windowPlatform = SumPlatformBilled(windowRows); // billable

        // ── Month-to-date + linear run-rate projection (platform-billed only) ──
        var mtdPlatform = SumPlatformBilled(mtdRows);
        var projected = daysElapsed <= 0 ? 0m : mtdPlatform / daysElapsed * daysInMonth;

        // ── Budget join (read-only; tenant GUID string is the account scope key) ──
        var budget = await budgets.GetAsync(tenantId, tenantId.ToString(), ct);
        var hasBudget = budget is not null && budget.LimitUsd > 0m;
        decimal? budgetUsd = budget?.LimitUsd;
        double? alertThreshold = budget?.AlertThreshold;
        double? budgetUtil = hasBudget ? (double)(mtdPlatform / budget!.LimitUsd) : null;
        double? projectedUtil = hasBudget ? (double)(projected / budget!.LimitUsd) : null;
        var projectedToExceed = budget is not null && projected > budget.LimitUsd;

        var summary = new CostSummary(
            windowByok, mtdPlatform, projected,
            budgetUsd, alertThreshold, budgetUtil, projectedUtil, projectedToExceed);

        // ── Trend vs prior equivalent window ──
        var priorByok = SumByokCost(priorRows);
        var priorPlatform = SumPlatformBilled(priorRows);
        var trend = new CostTrend(
            priorPlatform, DeltaPct(windowPlatform, priorPlatform),
            priorByok, DeltaPct(windowByok, priorByok));

        logger.LogInformation(
            "Tenant cost analytics served: tenantId={TenantId} from={From:o} to={To:o} "
                + "groupBy={GroupBy} rows={Rows} mtdPlatformBilledUsd={Mtd} "
                + "projectedPlatformBilledUsd={Projected} durationMs={DurationMs}",
            tenantId, window.From, window.To, groupBy?.ToWire() ?? "(none)",
            series.Count, mtdPlatform, projected, sw.ElapsedMilliseconds);

        // ── Best-effort, per-period deduped budget-exceeded DCB event ──
        if (projectedToExceed)
        {
            await TryEmitBudgetExceededAsync(
                tenantId, mode, budget!, mtdPlatform, projected, window, monthStart, ct);
        }

        return new CostAnalyticsResponse(
            tenantId,
            new CostWindow(DateOnly.FromDateTime(window.From), DateOnly.FromDateTime(window.To)),
            groupBy?.ToWire(),
            series,
            summary,
            trend);
    }

    /// <summary>
    /// Half-open <c>[from, to)</c> read against <c>analytics_usage_daily</c>,
    /// projecting only the cost columns this endpoint needs.
    /// </summary>
    private static async Task<List<CostRow>> LoadDailyAsync(
        TenantDbContext db, DateTime from, DateTime to, CancellationToken ct)
    {
        if (to <= from)
        {
            return [];
        }

        return await db.AnalyticsUsageDaily
            .Where(r => r.Day >= from && r.Day < to)
            .Select(r => new CostRow(
                r.Day, r.Provider, r.AgentId, r.CostBasis, r.CostUsd, r.PlatformBilledUsd))
            .ToListAsync(ct);
    }

    private List<CostSeriesBucket> BuildSeries(List<CostRow> rows, AnalyticsDimension? groupBy)
    {
        if (groupBy is null)
        {
            return rows
                .GroupBy(r => r.Day)
                .OrderBy(g => g.Key)
                .Select(g => new CostSeriesBucket(
                    DateOnly.FromDateTime(g.Key), null,
                    SumByokCost(g), SumPlatformBilled(g)))
                .ToList();
        }

        var keyOf = DimensionKeySelector(groupBy.Value);
        return rows
            .GroupBy(r => new SeriesKey(r.Day, keyOf(r) ?? Unattributed))
            .OrderBy(g => g.Key.Day)
            .ThenBy(g => g.Key.Group, StringComparer.Ordinal)
            .Select(g => new CostSeriesBucket(
                DateOnly.FromDateTime(g.Key.Day), g.Key.Group,
                SumByokCost(g), SumPlatformBilled(g)))
            .ToList();
    }

    private static Func<CostRow, string?> DimensionKeySelector(AnalyticsDimension dim) => dim switch
    {
        AnalyticsDimension.Provider => r => r.Provider,
        AnalyticsDimension.Agent => r => r.AgentId,
        // Only Provider/Agent are wired for cost (the endpoint rejects the rest);
        // fall back to a single unattributed bucket rather than throw.
        _ => _ => null,
    };

    /// <summary>Σ <c>CostUsd</c> of BYOK rows — the raw provider cost, informational only.</summary>
    private static decimal SumByokCost(IEnumerable<CostRow> rows)
    {
        decimal sum = 0m;
        foreach (var r in rows)
        {
            if (r.CostBasis == CostBasis.Byok)
            {
                sum += r.CostUsd;
            }
        }

        return sum;
    }

    /// <summary>
    /// Σ <c>PlatformBilledUsd</c> — the materialised billable amount (single
    /// source of truth). BYOK rows carry 0 (Story 36-2), so summing every row
    /// yields the platform-billed total with no cost-basis branch, and a
    /// fully-BYOK tenant is structurally 0.
    /// </summary>
    private static decimal SumPlatformBilled(IEnumerable<CostRow> rows)
    {
        decimal sum = 0m;
        foreach (var r in rows)
        {
            sum += r.PlatformBilledUsd;
        }

        return sum;
    }

    /// <summary>
    /// Percentage delta of <paramref name="current"/> vs <paramref name="prior"/>;
    /// <c>null</c> when the prior window is empty (no divide-by-zero).
    /// </summary>
    private static double? DeltaPct(decimal current, decimal prior) =>
        prior == 0m ? null : (double)((current - prior) / prior);

    private async Task TryEmitBudgetExceededAsync(
        Guid tenantId, string mode, BudgetConfig budget,
        decimal mtdPlatform, decimal projected, AnalyticsWindow window,
        DateTime monthStart, CancellationToken ct)
    {
        var period = $"{monthStart:yyyy-MM}";
        try
        {
            // Dedup keyed (tenantId, period): a hot dashboard polling this
            // endpoint emits at most one event per tenant per calendar period.
            var last = await events.GetLastByTypeAsync(
                tenantId, CostAnalyticsEvents.BudgetProjectedExceeded);
            if (last is not null && TryReadPeriodTag(last.Tags) == period)
            {
                logger.LogDebug(
                    "Budget-exceeded event already present this period {Period} for "
                        + "tenant {TenantId}; skipping (dedup).",
                    period, tenantId);
                return;
            }

            var windowTag =
                $"{DateOnly.FromDateTime(window.From):yyyy-MM-dd}..{DateOnly.FromDateTime(window.To):yyyy-MM-dd}";

            await events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = CostAnalyticsEvents.BudgetProjectedExceeded,
                TenantId = tenantId,
                Tags = JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["tenantId"] = tenantId.ToString(),
                    ["mode"] = mode,
                    ["period"] = period,
                }),
                Data = JsonSerializer.Serialize(new
                {
                    budgetUsd = budget.LimitUsd,
                    projectedPlatformBilledUsd = projected,
                    mtdPlatformBilledUsd = mtdPlatform,
                    window = windowTag,
                }),
                Metadata = JsonSerializer.Serialize(new
                {
                    workflowVersion = "1.0.0",
                    eventSource = "system",
                }),
            });

            logger.LogInformation(
                "Budget-projected-exceeded event emitted: tenantId={TenantId} budgetUsd={Budget} "
                    + "projectedPlatformBilledUsd={Projected} period={Period}",
                tenantId, budget.LimitUsd, projected, period);
        }
        catch (Exception ex)
        {
            // Best-effort: a failed append is logged WARN and never propagates
            // into the read path — the response still returns.
            logger.LogWarning(
                ex,
                "Budget-projected-exceeded event append failed for tenant {TenantId} "
                    + "(best-effort; the cost response is still returned).",
                tenantId);
        }
    }

    private static string? TryReadPeriodTag(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(tagsJson);
            return doc.RootElement.TryGetProperty("period", out var p)
                ? p.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Lightweight projection of a fact row (only the cost columns).</summary>
    private readonly record struct CostRow(
        DateTime Day,
        string? Provider,
        string? AgentId,
        CostBasis CostBasis,
        decimal CostUsd,
        decimal PlatformBilledUsd);

    /// <summary>Grouping key for a grouped series — a <c>(day, dimension-value)</c> tuple.</summary>
    private readonly record struct SeriesKey(DateTime Day, string Group);
}
