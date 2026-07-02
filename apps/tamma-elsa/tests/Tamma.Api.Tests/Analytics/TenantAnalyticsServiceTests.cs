using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Analytics;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Analytics;

/// <summary>
/// Story 36-3 — unit tests for the tenant usage analytics read seam.
///
/// <para>Two layers, no Postgres:</para>
/// <list type="number">
///   <item><description><b>Window clamp / UTC</b> — pure
///     <see cref="AnalyticsWindow.Resolve"/> matrix (defaults, 365-day max,
///     hour cap, from&gt;=to, bad enum, forced-UTC equivalence).</description></item>
///   <item><description><b>Grouping shape / reconciliation</b> — the service
///     against a seeded EF-InMemory <see cref="TenantDbContext"/>: ungrouped =
///     one summed row per bucket; grouped = one row per (bucket, dimension) with
///     the NULL key surfaced; Σ(grouped per bucket) == ungrouped bucket; breakdown
///     top-N by each metric.</description></item>
/// </list>
///
/// <para>Physical schema isolation + real Npgsql GROUP BY / timestamptz window
/// selection are proven in <see cref="TenantAnalyticsIntegrationTests"/> (a real
/// Postgres 17 container) — InMemory models neither.</para>
/// </summary>
[TestFixture]
public class TenantAnalyticsServiceTests
{
    private static readonly DateTime D1 = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime D2 = new(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowFrom = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowTo = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    // ─────────────────────────── Window clamp / UTC ───────────────────────────

    [Test]
    public void Resolve_Defaults_To30DayDailyWindow_WhenBothOmitted()
    {
        var w = AnalyticsWindow.Resolve(null, null, null);

        w.IsValid.Should().BeTrue();
        w.Granularity.Should().Be(AnalyticsGranularity.Day);
        (w.To - w.From).TotalDays.Should().BeApproximately(30, 0.01);
        w.From.Kind.Should().Be(DateTimeKind.Utc);
        w.To.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Test]
    public void Resolve_Truncates_WindowWiderThan365Days_ToMostRecent365()
    {
        var to = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var from = to.AddDays(-400);

        var w = AnalyticsWindow.Resolve(from, to, "day");

        w.IsValid.Should().BeTrue();
        w.To.Should().Be(to);
        w.From.Should().Be(to.AddDays(-AnalyticsWindow.MaxRangeDays),
            "a >365-day window truncates to the most-recent 365 days (clamp, not reject)");
    }

    [Test]
    public void Resolve_Rejects_HourGranularity_OverTheHourCap()
    {
        var to = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var from = to.AddDays(-40); // > 31-day hour cap

        var w = AnalyticsWindow.Resolve(from, to, "hour");

        w.IsValid.Should().BeFalse();
        w.Error.Should().Contain("hour");
    }

    [Test]
    public void Resolve_Allows_HourGranularity_WithinTheHourCap()
    {
        var to = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var from = to.AddDays(-10);

        var w = AnalyticsWindow.Resolve(from, to, "hour");

        w.IsValid.Should().BeTrue();
        w.Granularity.Should().Be(AnalyticsGranularity.Hour);
    }

    [Test]
    public void Resolve_Rejects_FromNotBeforeTo()
    {
        var t = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        AnalyticsWindow.Resolve(t, t, "day").IsValid.Should().BeFalse();
        AnalyticsWindow.Resolve(t.AddDays(1), t, "day").IsValid.Should().BeFalse();
    }

    [Test]
    public void Resolve_Rejects_UnknownGranularity()
    {
        var w = AnalyticsWindow.Resolve(WindowFrom, WindowTo, "weekly");
        w.IsValid.Should().BeFalse();
        w.Error.Should().Contain("granularity");
    }

    [Test]
    public void Resolve_ForcesUtc_SoAnOffsetFromSelectsTheSameWindowAsItsUtcEquivalent()
    {
        var utcFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc);

        // Same instant as utcFrom, but Kind=Local (what a non-UTC-offset bind
        // yields). ToLocalTime()/ToUniversalTime() round-trip is deterministic
        // on any machine TZ.
        var localFrom = utcFrom.ToLocalTime();

        var wUtc = AnalyticsWindow.Resolve(utcFrom, to, "day");
        var wLocal = AnalyticsWindow.Resolve(localFrom, to, "day");

        wLocal.From.Should().Be(wUtc.From, "forced-UTC normalization collapses both to the same instant");
        wUtc.From.Kind.Should().Be(DateTimeKind.Utc);
    }

    // ─────────────────────── Grouping shape / reconciliation ───────────────────────

    [Test]
    public async Task GetUsage_Ungrouped_SumsAllDimensionsPerBucket_OneRowPerBucket()
    {
        var (service, tenantId) = await SeededServiceAsync();
        var window = AnalyticsWindow.Resolve(WindowFrom, WindowTo, "day");

        var res = await service.GetUsageAsync(tenantId, window, groupBy: null, CancellationToken.None);

        res.GroupBy.Should().BeNull();
        res.Granularity.Should().Be("day");
        res.PeriodStart.Should().Be(window.From);
        res.PeriodEnd.Should().Be(window.To);
        res.Rows.Should().HaveCount(2, "two seeded buckets → one summed row each");

        var d1 = res.Rows.Single(r => r.Period == D1);
        d1.Key.Should().BeNull();
        d1.WorkflowsStarted.Should().Be(8);
        d1.WorkflowsCompleted.Should().Be(6);
        d1.WorkflowsFailed.Should().Be(2);
        d1.AgentDispatches.Should().Be(4);
        d1.TokensIn.Should().Be(300);
        d1.TokensOut.Should().Be(130);
        d1.CostUsd.Should().Be(3.0m);

        var d2 = res.Rows.Single(r => r.Period == D2);
        d2.TokensIn.Should().Be(300);
        d2.CostUsd.Should().Be(3.0m);
    }

    [Test]
    public async Task GetUsage_GroupByProvider_OneRowPerBucketDimension_NullKeyPreserved()
    {
        var (service, tenantId) = await SeededServiceAsync();
        var window = AnalyticsWindow.Resolve(WindowFrom, WindowTo, "day");

        var res = await service.GetUsageAsync(
            tenantId, window, AnalyticsDimension.Provider, CancellationToken.None);

        res.GroupBy.Should().Be("provider");
        // D1: anthropic, openai, NULL ; D2: anthropic
        res.Rows.Should().HaveCount(4);
        res.Rows.Should().ContainSingle(r => r.Period == D1 && r.Key == "anthropic");
        res.Rows.Should().ContainSingle(r => r.Period == D1 && r.Key == "openai");
        res.Rows.Should().ContainSingle(r => r.Period == D1 && r.Key == null)
            .Which.WorkflowsStarted.Should().Be(5, "the unattributed NULL-provider bucket is surfaced, never dropped");
    }

    [Test]
    public async Task GetUsage_GroupedRows_ReconcileToTheUngroupedBucketTotal()
    {
        var (service, tenantId) = await SeededServiceAsync();
        var window = AnalyticsWindow.Resolve(WindowFrom, WindowTo, "day");

        var ungrouped = await service.GetUsageAsync(tenantId, window, null, CancellationToken.None);

        foreach (var dim in new[]
        {
            AnalyticsDimension.Provider, AnalyticsDimension.Agent,
            AnalyticsDimension.Workflow, AnalyticsDimension.Repo,
        })
        {
            var grouped = await service.GetUsageAsync(tenantId, window, dim, CancellationToken.None);

            foreach (var bucket in ungrouped.Rows)
            {
                var partsForBucket = grouped.Rows.Where(r => r.Period == bucket.Period).ToList();
                partsForBucket.Sum(r => r.TokensIn).Should().Be(bucket.TokensIn,
                    $"Σ(grouped by {dim}) must equal the ungrouped bucket total (36-2 reconciliation)");
                partsForBucket.Sum(r => r.WorkflowsStarted).Should().Be(bucket.WorkflowsStarted);
                partsForBucket.Sum(r => r.AgentDispatches).Should().Be(bucket.AgentDispatches);
                partsForBucket.Sum(r => r.CostUsd).Should().Be(bucket.CostUsd);
            }
        }
    }

    [Test]
    public async Task GetUsage_HourGranularity_ReadsTheHourlyTable()
    {
        var tenantId = Guid.NewGuid();
        var db = $"tenant_analytics_hourly_{Guid.NewGuid():N}";
        await using (var seed = NewContext(db, tenantId))
        {
            seed.AnalyticsUsageHourly.Add(NewHourly(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc), "anthropic", tokensIn: 10, tokensOut: 5));
            seed.AnalyticsUsageHourly.Add(NewHourly(new DateTime(2026, 5, 1, 13, 0, 0, DateTimeKind.Utc), "anthropic", tokensIn: 20, tokensOut: 7));
            await seed.SaveChangesAsync();
        }

        var service = new TenantAnalyticsService(new InMemoryFactory(db), NullLogger<TenantAnalyticsService>.Instance);
        var window = AnalyticsWindow.Resolve(
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
            "hour");

        var res = await service.GetUsageAsync(tenantId, window, null, CancellationToken.None);

        res.Granularity.Should().Be("hour");
        res.Rows.Should().HaveCount(2, "two distinct hour buckets");
        res.Rows.Sum(r => r.TokensIn).Should().Be(30);
    }

    // ──────────────────────────────── Breakdown ────────────────────────────────

    [Test]
    public async Task GetBreakdown_ByProvider_TokensMetric_OrdersDescending_AndClampsLimit()
    {
        var (service, tenantId) = await SeededServiceAsync();
        var window = AnalyticsWindow.Resolve(WindowFrom, WindowTo, "day");

        var res = await service.GetBreakdownAsync(
            tenantId, window, AnalyticsDimension.Provider, AnalyticsMetric.Tokens, limit: 2, CancellationToken.None);

        res.Dimension.Should().Be("provider");
        res.Metric.Should().Be("tokens");
        res.Rows.Should().HaveCount(2, "limit=2 keeps only the top two dimension values");
        res.Rows[0].Key.Should().Be("anthropic");
        res.Rows[0].Value.Should().Be(550, "anthropic tokens = (100+50)+(300+100)");
        res.Rows[1].Key.Should().Be("openai");
        res.Rows[1].Value.Should().Be(280);
    }

    [Test]
    public async Task GetBreakdown_CostMetric_RanksByCost_AndIncludesNullKey()
    {
        var (service, tenantId) = await SeededServiceAsync();
        var window = AnalyticsWindow.Resolve(WindowFrom, WindowTo, "day");

        var res = await service.GetBreakdownAsync(
            tenantId, window, AnalyticsDimension.Provider, AnalyticsMetric.Cost, limit: 100, CancellationToken.None);

        res.Rows.Should().HaveCount(3, "anthropic, openai, and the NULL-provider bucket");
        res.Rows[0].Key.Should().Be("anthropic");
        res.Rows[0].Value.Should().Be(4.0m);
        res.Rows.Should().Contain(r => r.Key == null, "the unattributed bucket is ranked, never dropped");
    }

    // ──────────────────────────────── Helpers ────────────────────────────────

    private static async Task<(TenantAnalyticsService Service, Guid TenantId)> SeededServiceAsync()
    {
        var tenantId = Guid.NewGuid();
        var db = $"tenant_analytics_{Guid.NewGuid():N}";
        await using (var seed = NewContext(db, tenantId))
        {
            seed.AnalyticsUsageDaily.AddRange(
                NewDaily(D1, "anthropic", "a1", tokensIn: 100, tokensOut: 50, cost: 1.0m, ws: 2, wc: 2, wf: 0, ad: 3),
                NewDaily(D1, "openai", "a2", tokensIn: 200, tokensOut: 80, cost: 2.0m, ws: 1, wc: 0, wf: 1, ad: 1),
                NewDaily(D1, provider: null, agentId: null, tokensIn: 0, tokensOut: 0, cost: 0m, ws: 5, wc: 4, wf: 1, ad: 0),
                NewDaily(D2, "anthropic", "a1", tokensIn: 300, tokensOut: 100, cost: 3.0m, ws: 1, wc: 1, wf: 0, ad: 2));
            await seed.SaveChangesAsync();
        }

        var service = new TenantAnalyticsService(new InMemoryFactory(db), NullLogger<TenantAnalyticsService>.Instance);
        return (service, tenantId);
    }

    private static TenantDbContext NewContext(string dbName, Guid tenantId) =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseInMemoryDatabase(dbName).Options, tenantId);

    private static AnalyticsUsageDaily NewDaily(
        DateTime day, string? provider, string? agentId,
        long tokensIn, long tokensOut, decimal cost, long ws, long wc, long wf, long ad) => new()
    {
        Id = Guid.NewGuid(),
        Day = day,
        Provider = provider,
        AgentId = agentId,
        WorkflowDefinitionId = null,
        RepoId = null,
        CostBasis = CostBasis.Platform,
        TokensIn = tokensIn,
        TokensOut = tokensOut,
        CostUsd = cost,
        PlatformBilledUsd = cost,
        WorkflowsStarted = ws,
        WorkflowsCompleted = wc,
        WorkflowsFailed = wf,
        AgentDispatches = ad,
        ComputedAt = DateTime.UtcNow,
    };

    private static AnalyticsUsageHourly NewHourly(
        DateTime hour, string provider, long tokensIn, long tokensOut) => new()
    {
        Id = Guid.NewGuid(),
        Hour = hour,
        Provider = provider,
        CostBasis = CostBasis.Byok,
        TokensIn = tokensIn,
        TokensOut = tokensOut,
        ComputedAt = DateTime.UtcNow,
    };

    /// <summary>Fake factory that hands out contexts over one shared InMemory database.</summary>
    private sealed class InMemoryFactory(string dbName) : ITenantDbContextFactory
    {
        public ValueTask<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            new(new TenantDbContext(
                new DbContextOptionsBuilder<TenantDbContext>().UseInMemoryDatabase(dbName).Options, tenantId));
    }
}
