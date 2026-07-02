using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Analytics;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Analytics;

/// <summary>
/// Story 36-4 — unit tests for the cost/spend read seam (EF-InMemory; no
/// Postgres). Covers the BYOK/platform split (AC2/AC5), month-to-date + linear
/// run-rate projection against a fixed clock (AC3), the <c>BudgetConfig</c> join
/// + budget-vs-actual (AC3/AC7), grouping with <c>NULL→"unattributed"</c>
/// reconciliation (AC2), the read-only / no-re-markup contract (AC4/AC10), trend
/// (AC6), the budget-exceeded DCB event + per-period dedup + swallowed failure
/// (AC9), and empty-state (AC12). Physical isolation + the fully-BYOK end-to-end
/// split are proven on real Postgres in <see cref="CostAnalyticsIsolationTests"/>.
/// </summary>
[TestFixture]
public class CostAnalyticsServiceTests
{
    private static readonly DateTimeOffset July15 =
        new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static AnalyticsWindow JulyWindow() =>
        AnalyticsWindow.Resolve(Utc(2026, 7, 1), Utc(2026, 8, 1), "day");

    // ─────────────────────────── BYOK / platform split ───────────────────────────

    [Test]
    public async Task GetCost_SplitsByokCostFromPlatformBilled_ByokRowsNeverContributeToPlatform()
    {
        var ctx = await SeedAsync(July15, budget: null,
            Daily(Utc(2026, 7, 3), CostBasis.Byok, cost: 5m, billed: 0m),
            Daily(Utc(2026, 7, 4), CostBasis.Platform, cost: 10m, billed: 12m));

        var res = await ctx.Svc.GetCostAsync(ctx.TenantId, JulyWindow(), null, "saas", CancellationToken.None);

        res.Series.Should().HaveCount(2);
        var byokDay = res.Series.Single(s => s.Day == new DateOnly(2026, 7, 3));
        byokDay.ByokCostUsd.Should().Be(5m);
        byokDay.PlatformBilledUsd.Should().Be(0m, "a BYOK row carries PlatformBilledUsd=0");

        var platDay = res.Series.Single(s => s.Day == new DateOnly(2026, 7, 4));
        platDay.ByokCostUsd.Should().Be(0m, "a platform row never adds to byokCostUsd");
        platDay.PlatformBilledUsd.Should().Be(12m);

        res.Summary.ByokCostUsd.Should().Be(5m, "window BYOK total is informational");
    }

    [Test]
    public async Task GetCost_PlatformBilled_IsTheMaterialisedColumn_NotRawCost_NoReMarkup()
    {
        // billed (12) != cost (10) and != cost×anyMargin — proving the endpoint
        // reads the materialised PlatformBilledUsd column, never recomputes it.
        var ctx = await SeedAsync(July15, budget: null,
            Daily(Utc(2026, 7, 5), CostBasis.Platform, cost: 10m, billed: 12m));

        var res = await ctx.Svc.GetCostAsync(ctx.TenantId, JulyWindow(), null, "saas", CancellationToken.None);

        res.Series.Single().PlatformBilledUsd.Should().Be(12m);
        res.Summary.MtdPlatformBilledUsd.Should().Be(12m);
    }

    [Test]
    public void CostAnalyticsService_HasNoMarkupOrPricingDependency_AC4_AC10()
    {
        var ctorParamNames = typeof(CostAnalyticsService)
            .GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType.Name).ToList();

        ctorParamNames.Should().NotContain(
            n => n.Contains("Pricing") || n.Contains("Markup") || n.Contains("Margin"),
            "the cost endpoint reads PlatformBilledUsd as the single source of truth — "
                + "it must not depend on the Story 34-5 markup engine or any pricing config");
    }

    // ──────────────────────── Projection (fixed clock) ────────────────────────

    [Test]
    public async Task GetCost_Projection_IsLinearRunRate_MtdOverDaysElapsedTimesDaysInMonth()
    {
        // day 15 of 31; mtd platform-billed = 150 → projected = 150/15*31 = 310.
        var ctx = await SeedAsync(July15, budget: null,
            Daily(Utc(2026, 7, 2), CostBasis.Platform, cost: 50m, billed: 50m),
            Daily(Utc(2026, 7, 8), CostBasis.Platform, cost: 100m, billed: 100m));

        var res = await ctx.Svc.GetCostAsync(ctx.TenantId, JulyWindow(), null, "saas", CancellationToken.None);

        res.Summary.MtdPlatformBilledUsd.Should().Be(150m);
        res.Summary.ProjectedPlatformBilledUsd.Should().Be(310m);
    }

    [Test]
    public async Task GetCost_MtdExcludesRowsDatedAfterToday_ProjectionIsToDate()
    {
        var ctx = await SeedAsync(July15, budget: null,
            Daily(Utc(2026, 7, 10), CostBasis.Platform, cost: 100m, billed: 100m),
            Daily(Utc(2026, 7, 20), CostBasis.Platform, cost: 999m, billed: 999m)); // future — excluded from MTD

        var res = await ctx.Svc.GetCostAsync(ctx.TenantId, JulyWindow(), null, "saas", CancellationToken.None);

        res.Summary.MtdPlatformBilledUsd.Should().Be(100m, "MTD is to-date; the July-20 row is in the future");
        res.Summary.ProjectedPlatformBilledUsd.Should().Be(100m / 15 * 31);
    }

    [Test]
    public async Task GetCost_ZeroMtd_ProjectsZero_NoDivideByZero()
    {
        var ctx = await SeedAsync(July15, budget: null); // no rows at all

        var res = await ctx.Svc.GetCostAsync(ctx.TenantId, JulyWindow(), null, "saas", CancellationToken.None);

        res.Summary.MtdPlatformBilledUsd.Should().Be(0m);
        res.Summary.ProjectedPlatformBilledUsd.Should().Be(0m);
    }

    // ──────────────────────── Budget join / budget-vs-actual ────────────────────────

    [Test]
    public async Task GetCost_WithBudget_ReportsUtilizationAndProjectedToExceed()
    {
        // day 15; mtd = 300 → projected = 620. budget = 400 → projected exceeds.
        var budget = new BudgetConfig { LimitUsd = 400m, AlertThreshold = 0.8, PeriodDays = 30 };
        var ctx = await SeedAsync(July15, budget,
            Daily(Utc(2026, 7, 5), CostBasis.Platform, cost: 300m, billed: 300m));

        var res = await ctx.Svc.GetCostAsync(ctx.TenantId, JulyWindow(), null, "saas", CancellationToken.None);

        res.Summary.BudgetUsd.Should().Be(400m);
        res.Summary.AlertThreshold.Should().Be(0.8);
        res.Summary.BudgetUtilizationPct.Should().BeApproximately(0.75, 1e-9);     // 300/400
        res.Summary.ProjectedUtilizationPct.Should().BeApproximately(1.55, 1e-9);  // 620/400
        res.Summary.ProjectedToExceedBudget.Should().BeTrue();
    }

    [Test]
    public async Task GetCost_NoBudget_UtilizationFieldsAreNull_SeriesStillReturned()
    {
        var ctx = await SeedAsync(July15, budget: null,
            Daily(Utc(2026, 7, 5), CostBasis.Platform, cost: 100m, billed: 100m));

        var res = await ctx.Svc.GetCostAsync(ctx.TenantId, JulyWindow(), null, "saas", CancellationToken.None);

        res.Summary.BudgetUsd.Should().BeNull();
        res.Summary.AlertThreshold.Should().BeNull();
        res.Summary.BudgetUtilizationPct.Should().BeNull();
        res.Summary.ProjectedUtilizationPct.Should().BeNull();
        res.Summary.ProjectedToExceedBudget.Should().BeFalse();
        res.Series.Should().ContainSingle();
        res.Summary.ProjectedPlatformBilledUsd.Should().Be(100m / 15 * 31);
    }

    [Test]
    public async Task GetCost_StoredZeroLimitBudget_TreatedAsNoMeaningfulBudget_NoExceedNoEvent()
    {
        // A persisted BudgetConfig with LimitUsd = 0 (e.g. BudgetConfigDefaults.DefaultLimitUsd)
        // plus real platform spend must be consistent: projectedToExceedBudget=false and null
        // utilisation pcts (the "no meaningful budget" story), NOT the asymmetric
        // projectedToExceed:true / budgetUsd:0 mix — and NO spurious budget-exceeded event.
        var budget = new BudgetConfig { LimitUsd = 0m, AlertThreshold = 0.8, PeriodDays = 30 };
        var ctx = await SeedAsync(July15, budget,
            Daily(Utc(2026, 7, 5), CostBasis.Platform, cost: 300m, billed: 300m));

        var res = await ctx.Svc.GetCostAsync(ctx.TenantId, JulyWindow(), null, "saas", CancellationToken.None);

        res.Summary.ProjectedToExceedBudget.Should().BeFalse(
            "a LimitUsd <= 0 budget is not a meaningful budget to exceed");
        res.Summary.BudgetUtilizationPct.Should().BeNull();
        res.Summary.ProjectedUtilizationPct.Should().BeNull();
        ctx.Events.Verify(e => e.AppendAsync(It.IsAny<DomainEvent>()), Times.Never);
    }

    // ───────────────────────── Grouping + reconciliation ─────────────────────────

    [Test]
    public async Task GetCost_GroupByProvider_NullSurfacedAsUnattributed_ReconcilesToTotal()
    {
        var ctx = await SeedAsync(July15, budget: null,
            Daily(Utc(2026, 7, 6), CostBasis.Byok, cost: 4m, billed: 0m, provider: "anthropic"),
            Daily(Utc(2026, 7, 6), CostBasis.Platform, cost: 6m, billed: 6m, provider: "openai"),
            Daily(Utc(2026, 7, 6), CostBasis.Platform, cost: 2m, billed: 3m, provider: null));

        var grouped = await ctx.Svc.GetCostAsync(
            ctx.TenantId, JulyWindow(), AnalyticsDimension.Provider, "saas", CancellationToken.None);
        var ungrouped = await ctx.Svc.GetCostAsync(
            ctx.TenantId, JulyWindow(), null, "saas", CancellationToken.None);

        grouped.GroupBy.Should().Be("provider");
        grouped.Series.Should().HaveCount(3);
        grouped.Series.Should().ContainSingle(s => s.Group == "unattributed")
            .Which.PlatformBilledUsd.Should().Be(3m, "the NULL-provider bucket is surfaced, never dropped");

        var day = new DateOnly(2026, 7, 6);
        grouped.Series.Where(s => s.Day == day).Sum(s => s.ByokCostUsd)
            .Should().Be(ungrouped.Series.Single(s => s.Day == day).ByokCostUsd);
        grouped.Series.Where(s => s.Day == day).Sum(s => s.PlatformBilledUsd)
            .Should().Be(ungrouped.Series.Single(s => s.Day == day).PlatformBilledUsd);
    }

    [Test]
    public async Task GetCost_GroupByAgent_NullSurfacedAsUnattributed()
    {
        var ctx = await SeedAsync(July15, budget: null,
            Daily(Utc(2026, 7, 6), CostBasis.Platform, cost: 6m, billed: 6m, agent: "reviewer"),
            Daily(Utc(2026, 7, 6), CostBasis.Platform, cost: 2m, billed: 3m, agent: null));

        var grouped = await ctx.Svc.GetCostAsync(
            ctx.TenantId, JulyWindow(), AnalyticsDimension.Agent, "saas", CancellationToken.None);

        grouped.GroupBy.Should().Be("agent");
        grouped.Series.Should().Contain(s => s.Group == "reviewer");
        grouped.Series.Should().Contain(s => s.Group == "unattributed");
    }

    // ──────────────────────────────── Trend ────────────────────────────────

    [Test]
    public async Task GetCost_Trend_ComparesToPriorEquivalentWindow()
    {
        // window [Jul8, Jul15) len 7 → prior [Jul1, Jul8).
        var window = AnalyticsWindow.Resolve(Utc(2026, 7, 8), Utc(2026, 7, 15), "day");
        var ctx = await SeedAsync(July15, budget: null,
            Daily(Utc(2026, 7, 10), CostBasis.Platform, cost: 120m, billed: 120m), // in-window
            Daily(Utc(2026, 7, 10), CostBasis.Byok, cost: 30m, billed: 0m),
            Daily(Utc(2026, 7, 3), CostBasis.Platform, cost: 100m, billed: 100m),  // prior
            Daily(Utc(2026, 7, 3), CostBasis.Byok, cost: 20m, billed: 0m));

        var res = await ctx.Svc.GetCostAsync(ctx.TenantId, window, null, "saas", CancellationToken.None);

        res.Trend.PlatformBilledUsd.Should().Be(100m, "prior-window platform-billed total");
        res.Trend.PlatformBilledDeltaPct.Should().BeApproximately(0.20, 1e-9); // (120-100)/100
        res.Trend.ByokCostUsd.Should().Be(20m);
        res.Trend.ByokCostDeltaPct.Should().BeApproximately(0.50, 1e-9);       // (30-20)/20
    }

    [Test]
    public async Task GetCost_Trend_EmptyPriorWindow_YieldsNullDelta()
    {
        var window = AnalyticsWindow.Resolve(Utc(2026, 7, 8), Utc(2026, 7, 15), "day");
        var ctx = await SeedAsync(July15, budget: null,
            Daily(Utc(2026, 7, 10), CostBasis.Platform, cost: 120m, billed: 120m)); // in-window only

        var res = await ctx.Svc.GetCostAsync(ctx.TenantId, window, null, "saas", CancellationToken.None);

        res.Trend.PlatformBilledUsd.Should().Be(0m);
        res.Trend.PlatformBilledDeltaPct.Should().BeNull();
        res.Trend.ByokCostDeltaPct.Should().BeNull();
    }

    // ──────────────────────── Budget-exceeded DCB event ────────────────────────

    [Test]
    public async Task GetCost_ProjectionOverBudget_EmitsOneBudgetExceededEvent()
    {
        var budget = new BudgetConfig { LimitUsd = 400m, AlertThreshold = 0.8, PeriodDays = 30 };
        var ctx = await SeedAsync(July15, budget,
            Daily(Utc(2026, 7, 5), CostBasis.Platform, cost: 300m, billed: 300m)); // proj 620 > 400

        await ctx.Svc.GetCostAsync(ctx.TenantId, JulyWindow(), null, "saas", CancellationToken.None);

        ctx.Events.Verify(e => e.AppendAsync(It.Is<DomainEvent>(ev =>
            ev.Type == CostAnalyticsEvents.BudgetProjectedExceeded
            && ev.TenantId == ctx.TenantId
            && ev.Tags.Contains("\"period\":\"2026-07\"")
            && ev.Tags.Contains("\"mode\":\"saas\"")
            && ev.Data.Contains("projectedPlatformBilledUsd"))), Times.Once);
    }

    [Test]
    public async Task GetCost_ProjectionOverBudget_DedupsWithinTheSamePeriod()
    {
        var budget = new BudgetConfig { LimitUsd = 400m, AlertThreshold = 0.8, PeriodDays = 30 };
        var ctx = await SeedAsync(July15, budget,
            Daily(Utc(2026, 7, 5), CostBasis.Platform, cost: 300m, billed: 300m));

        // An event for this same period (2026-07) already exists → skip.
        ctx.Events.Setup(e => e.GetLastByTypeAsync(ctx.TenantId, CostAnalyticsEvents.BudgetProjectedExceeded))
            .ReturnsAsync(new DomainEvent
            {
                Type = CostAnalyticsEvents.BudgetProjectedExceeded,
                TenantId = ctx.TenantId,
                Tags = "{\"tenantId\":\"x\",\"mode\":\"saas\",\"period\":\"2026-07\"}",
            });

        await ctx.Svc.GetCostAsync(ctx.TenantId, JulyWindow(), null, "saas", CancellationToken.None);

        ctx.Events.Verify(e => e.AppendAsync(It.IsAny<DomainEvent>()), Times.Never);
    }

    [Test]
    public async Task GetCost_ProjectionOverBudget_EmitsAgainWhenLastEventIsAPriorPeriod()
    {
        var budget = new BudgetConfig { LimitUsd = 400m, AlertThreshold = 0.8, PeriodDays = 30 };
        var ctx = await SeedAsync(July15, budget,
            Daily(Utc(2026, 7, 5), CostBasis.Platform, cost: 300m, billed: 300m));

        ctx.Events.Setup(e => e.GetLastByTypeAsync(ctx.TenantId, CostAnalyticsEvents.BudgetProjectedExceeded))
            .ReturnsAsync(new DomainEvent
            {
                Type = CostAnalyticsEvents.BudgetProjectedExceeded,
                TenantId = ctx.TenantId,
                Tags = "{\"period\":\"2026-06\"}", // a different (prior) period
            });

        await ctx.Svc.GetCostAsync(ctx.TenantId, JulyWindow(), null, "saas", CancellationToken.None);

        ctx.Events.Verify(e => e.AppendAsync(It.IsAny<DomainEvent>()), Times.Once);
    }

    [Test]
    public async Task GetCost_ProjectionUnderBudget_EmitsNoEvent()
    {
        var budget = new BudgetConfig { LimitUsd = 100_000m, AlertThreshold = 0.8, PeriodDays = 30 };
        var ctx = await SeedAsync(July15, budget,
            Daily(Utc(2026, 7, 5), CostBasis.Platform, cost: 10m, billed: 10m));

        await ctx.Svc.GetCostAsync(ctx.TenantId, JulyWindow(), null, "saas", CancellationToken.None);

        ctx.Events.Verify(e => e.AppendAsync(It.IsAny<DomainEvent>()), Times.Never);
    }

    [Test]
    public async Task GetCost_EventAppendFailure_IsSwallowed_ResponseStillReturns()
    {
        var budget = new BudgetConfig { LimitUsd = 400m, AlertThreshold = 0.8, PeriodDays = 30 };
        var ctx = await SeedAsync(July15, budget,
            Daily(Utc(2026, 7, 5), CostBasis.Platform, cost: 300m, billed: 300m));

        ctx.Events.Setup(e => e.AppendAsync(It.IsAny<DomainEvent>()))
            .ThrowsAsync(new InvalidOperationException("event store down"));

        var act = async () => await ctx.Svc.GetCostAsync(
            ctx.TenantId, JulyWindow(), null, "saas", CancellationToken.None);

        var res = await act.Should().NotThrowAsync();
        res.Which.Summary.ProjectedToExceedBudget.Should().BeTrue();
    }

    // ──────────────────────────────── Empty state ────────────────────────────────

    [Test]
    public async Task GetCost_NoFactRows_ReturnsWellFormedEmptyResponse()
    {
        var ctx = await SeedAsync(July15, budget: null);

        var res = await ctx.Svc.GetCostAsync(ctx.TenantId, JulyWindow(), null, "saas", CancellationToken.None);

        res.Series.Should().BeEmpty();
        res.Summary.ByokCostUsd.Should().Be(0m);
        res.Summary.MtdPlatformBilledUsd.Should().Be(0m);
        res.Summary.ProjectedPlatformBilledUsd.Should().Be(0m);
        res.Trend.PlatformBilledUsd.Should().Be(0m);
        res.Trend.PlatformBilledDeltaPct.Should().BeNull();
        res.Window.From.Should().Be(new DateOnly(2026, 7, 1));
        res.Window.To.Should().Be(new DateOnly(2026, 8, 1));
    }

    // ──────────────────────────────── Helpers ────────────────────────────────

    private sealed record Ctx(
        CostAnalyticsService Svc,
        Guid TenantId,
        Mock<IEventRepository> Events,
        Mock<IBudgetConfigRepository> Budgets);

    private static async Task<Ctx> SeedAsync(
        DateTimeOffset now, BudgetConfig? budget, params AnalyticsUsageDaily[] rows)
    {
        var tenantId = Guid.NewGuid();
        var dbName = $"cost_{Guid.NewGuid():N}";
        await using (var seed = NewContext(dbName, tenantId))
        {
            seed.AnalyticsUsageDaily.AddRange(rows);
            await seed.SaveChangesAsync();
        }

        var budgets = new Mock<IBudgetConfigRepository>();
        budgets.Setup(b => b.GetAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(budget);

        var events = new Mock<IEventRepository>();
        events.Setup(e => e.GetLastByTypeAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync((DomainEvent?)null);
        events.Setup(e => e.AppendAsync(It.IsAny<DomainEvent>()))
            .ReturnsAsync((DomainEvent e) => e);

        var svc = new CostAnalyticsService(
            new InMemoryFactory(dbName), budgets.Object, events.Object,
            new FixedClock(now), NullLogger<CostAnalyticsService>.Instance);
        return new Ctx(svc, tenantId, events, budgets);
    }

    private static TenantDbContext NewContext(string dbName, Guid tenantId) =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseInMemoryDatabase(dbName).Options, tenantId);

    private static AnalyticsUsageDaily Daily(
        DateTime day, CostBasis basis, decimal cost, decimal billed,
        string? provider = null, string? agent = null) => new()
    {
        Id = Guid.NewGuid(),
        Day = day,
        Provider = provider,
        AgentId = agent,
        WorkflowDefinitionId = null,
        RepoId = null,
        CostBasis = basis,
        TokensIn = 0,
        TokensOut = 0,
        CostUsd = cost,
        PlatformBilledUsd = billed,
        WorkflowsStarted = 0,
        WorkflowsCompleted = 0,
        WorkflowsFailed = 0,
        AgentDispatches = 0,
        ComputedAt = DateTime.UtcNow,
    };

    private sealed class InMemoryFactory(string dbName) : ITenantDbContextFactory
    {
        public ValueTask<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            new(new TenantDbContext(
                new DbContextOptionsBuilder<TenantDbContext>().UseInMemoryDatabase(dbName).Options, tenantId));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
