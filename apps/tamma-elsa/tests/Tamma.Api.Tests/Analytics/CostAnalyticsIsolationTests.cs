using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.Analytics;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Analytics;

/// <summary>
/// Story 36-4 (AC5/AC11) — Postgres 17 Testcontainer proof for the cost/spend
/// read seam. EF-InMemory models neither schema isolation nor real
/// <c>timestamptz</c> selection, so a real Postgres is the only proof of:
///
/// <list type="number">
///   <item><description><b>Fully-BYOK ⇒ platform-billed = 0 (AC5).</b> A tenant
///     whose <c>analytics_usage_daily</c> rows are all <c>byok</c>
///     (<c>PlatformBilledUsd = 0</c>) reports <c>summary.platformBilledUsd == 0</c>
///     and <c>projectedPlatformBilledUsd == 0</c> while <c>byokCostUsd &gt; 0</c> —
///     markup on a BYOK call is structurally impossible here.</description></item>
///   <item><description><b>Physical isolation (AC11).</b> A request for tenant A
///     returns only A's spend; B's rows are unreachable through A's context (no
///     <c>TenantId</c> column, no query filter — the schema is the isolation
///     plane). The <c>BudgetConfig</c> join reads only A's row.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class CostAnalyticsIsolationTests
{
    private static readonly DateTimeOffset May15 = new(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);

    private PostgreSqlContainer _postgres = null!;
    private string _baseConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("cost_analytics_api_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _baseConnectionString = _postgres.GetConnectionString();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _postgres.DisposeAsync();

    private string CsFor(string schema) =>
        new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = schema }.ConnectionString;

    private static AnalyticsWindow MayWindow() => AnalyticsWindow.Resolve(
        new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), "day");

    // ───────────────────── AC5 — fully-BYOK ⇒ platformBilled = 0 ─────────────────────

    [Test]
    public async Task GetCost_FullyByokTenant_HasZeroPlatformBilled_ButNonZeroByokCost()
    {
        var tenant = Guid.NewGuid();
        var schema = TenantNaming.SchemaName(tenant);
        await new EfTenantDbMigrator().MigrateTenantAppAsync(CsFor(schema));

        await SeedDailyAsync(schema, tenant, new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc),
            CostBasis.Byok, cost: 5m, billed: 0m, provider: "anthropic");
        await SeedDailyAsync(schema, tenant, new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
            CostBasis.Byok, cost: 7m, billed: 0m, provider: "openai");

        var service = BuildService(tenant, schema, budgets: NoBudget());
        var res = await service.GetCostAsync(tenant, MayWindow(), null, "saas", CancellationToken.None);

        res.Summary.MtdPlatformBilledUsd.Should().Be(0m, "byok rows carry PlatformBilledUsd = 0");
        res.Summary.ProjectedPlatformBilledUsd.Should().Be(0m);
        res.Summary.ByokCostUsd.Should().Be(12m, "the informational raw provider cost is still reported");
        res.Series.Should().OnlyContain(s => s.PlatformBilledUsd == 0m);
    }

    // ───────────────────── AC11 — physical isolation + budget-join scope ─────────────────────

    [Test]
    public async Task GetCost_IsHardScopedToTheCallersSchema_IncludingTheBudgetJoin()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var schemaA = TenantNaming.SchemaName(tenantA);
        var schemaB = TenantNaming.SchemaName(tenantB);

        var migrator = new EfTenantDbMigrator();
        await migrator.MigrateTenantAppAsync(CsFor(schemaA));
        await migrator.MigrateTenantAppAsync(CsFor(schemaB));

        var day = new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc);
        await SeedDailyAsync(schemaA, tenantA, day, CostBasis.Platform, cost: 10m, billed: 12m, provider: "anthropic");
        await SeedDailyAsync(schemaB, tenantB, day, CostBasis.Platform, cost: 100m, billed: 200m, provider: "openai");

        // Budget only in tenant A's schema — proves the join is tenant-scoped.
        await SeedBudgetAsync(schemaA, tenantA, limitUsd: 1000m);

        var factory = new SchemaRoutingFactory(_baseConnectionString)
            .Map(tenantA, schemaA)
            .Map(tenantB, schemaB);
        // Real BudgetConfigRepository over the same routing factory → the join
        // physically reads each tenant's own schema.
        var budgets = new BudgetConfigRepository(factory, NullLogger<BudgetConfigRepository>.Instance);
        var service = new CostAnalyticsService(
            factory, budgets, NoOpEvents(), new FixedClock(May15), NullLogger<CostAnalyticsService>.Instance);

        var a = await service.GetCostAsync(tenantA, MayWindow(), null, "saas", CancellationToken.None);
        a.Summary.MtdPlatformBilledUsd.Should().Be(12m, "tenant A sees only its own spend");
        a.Series.Should().OnlyContain(s => s.PlatformBilledUsd != 200m, "tenant B's spend must never leak into A");
        a.Summary.BudgetUsd.Should().Be(1000m, "the budget join reads tenant A's BudgetConfig");

        var b = await service.GetCostAsync(tenantB, MayWindow(), null, "saas", CancellationToken.None);
        b.Summary.MtdPlatformBilledUsd.Should().Be(200m, "tenant B sees only its own spend");
        b.Summary.BudgetUsd.Should().BeNull("tenant B has no BudgetConfig row — the join is scoped to B");
    }

    // ──────────────────────────────── Helpers ────────────────────────────────

    private CostAnalyticsService BuildService(Guid tenant, string schema, IBudgetConfigRepository budgets)
    {
        var factory = new SchemaRoutingFactory(_baseConnectionString).Map(tenant, schema);
        return new CostAnalyticsService(
            factory, budgets, NoOpEvents(), new FixedClock(May15), NullLogger<CostAnalyticsService>.Instance);
    }

    private static IBudgetConfigRepository NoBudget()
    {
        var m = new Mock<IBudgetConfigRepository>();
        m.Setup(b => b.GetAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BudgetConfig?)null);
        return m.Object;
    }

    private static IEventRepository NoOpEvents()
    {
        var m = new Mock<IEventRepository>();
        m.Setup(e => e.GetLastByTypeAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync((DomainEvent?)null);
        m.Setup(e => e.AppendAsync(It.IsAny<DomainEvent>())).ReturnsAsync((DomainEvent e) => e);
        return m.Object;
    }

    private async Task SeedDailyAsync(
        string schema, Guid tenantId, DateTime day, CostBasis basis,
        decimal cost, decimal billed, string? provider)
    {
        var opts = new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(CsFor(schema)).Options;
        await using var ctx = new TenantDbContext(opts, tenantId);
        ctx.AnalyticsUsageDaily.Add(new AnalyticsUsageDaily
        {
            Id = Guid.NewGuid(),
            Day = day,
            Provider = provider,
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
        });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedBudgetAsync(string schema, Guid tenantId, decimal limitUsd)
    {
        var opts = new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(CsFor(schema)).Options;
        await using var ctx = new TenantDbContext(opts, tenantId);
        ctx.BudgetConfigs.Add(new BudgetConfig
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AccountId = tenantId.ToString(),
            LimitUsd = limitUsd,
            AlertThreshold = 0.8,
            PeriodDays = 30,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>Routes each tenant id to its own search-path schema connection.</summary>
    private sealed class SchemaRoutingFactory(string baseCs) : ITenantDbContextFactory
    {
        private readonly Dictionary<Guid, string> _schemas = new();

        public SchemaRoutingFactory Map(Guid tenantId, string schema)
        {
            _schemas[tenantId] = schema;
            return this;
        }

        public ValueTask<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct = default)
        {
            if (!_schemas.TryGetValue(tenantId, out var schema))
            {
                throw new InvalidOperationException($"Tenant {tenantId} not reachable.");
            }

            var cs = new NpgsqlConnectionStringBuilder(baseCs) { SearchPath = schema }.ConnectionString;
            var opts = new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(cs).Options;
            return new ValueTask<TenantDbContext>(new TenantDbContext(opts, tenantId));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
