using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.Analytics;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Analytics;

/// <summary>
/// Story 36-3 (AC4/AC12) — Postgres 17 Testcontainer proof for the tenant
/// usage analytics read seam. Two tenant schemas in one DB (search-path
/// isolation, per <c>AnalyticsUsageMigrationTests</c> /
/// <c>DimensionalRollupIsolationTests</c>). EF InMemory models neither schema
/// isolation nor real <c>timestamp with time zone</c> window selection, so a
/// real Postgres is the only proof of:
///
/// <list type="number">
///   <item><description><b>Isolation</b> — a tenant-A call returns only A's fact
///     rows; a tenant-B call returns only B's. A row in schema A is physically
///     unreachable through schema B's context (no <c>TenantId</c> column, no
///     query filter — the schema is the isolation plane).</description></item>
///   <item><description><b>Grouping + reconciliation</b> against real rows —
///     Σ(grouped per bucket) == the ungrouped bucket total, NULL dimension
///     surfaced.</description></item>
///   <item><description><b>Range clamp + UTC window</b> on real
///     <c>timestamptz</c> columns — a &gt;365-day window truncates to the
///     most-recent 365 days (the older row is excluded); a Local-kind
///     <c>from</c> selects the same buckets as its UTC equivalent.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class TenantAnalyticsIntegrationTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _baseConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tenant_analytics_api_test")
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

    // ─────────────────────────────── Isolation ───────────────────────────────

    [Test]
    public async Task GetUsage_IsHardScopedToTheCallersSchema_NoCrossTenantLeak()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var schemaA = TenantNaming.SchemaName(tenantA);
        var schemaB = TenantNaming.SchemaName(tenantB);

        var migrator = new EfTenantDbMigrator();
        await migrator.MigrateTenantAppAsync(CsFor(schemaA));
        await migrator.MigrateTenantAppAsync(CsFor(schemaB));

        var day = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedDailyAsync(schemaA, tenantA, day, "anthropic", tokensIn: 100, cost: 1.0m);
        await SeedDailyAsync(schemaB, tenantB, day, "openai", tokensIn: 999, cost: 9.0m);

        var factory = new SchemaRoutingFactory(_baseConnectionString)
            .Map(tenantA, schemaA)
            .Map(tenantB, schemaB);
        var service = new TenantAnalyticsService(factory, NullLogger<TenantAnalyticsService>.Instance);
        var window = AnalyticsWindow.Resolve(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), "day");

        var aResult = await service.GetUsageAsync(tenantA, window, null, CancellationToken.None);
        aResult.Rows.Should().ContainSingle();
        aResult.Rows[0].TokensIn.Should().Be(100, "tenant A sees only its own row");
        aResult.Rows.Should().NotContain(r => r.TokensIn == 999, "tenant B's row must never leak into A");

        var bResult = await service.GetUsageAsync(tenantB, window, null, CancellationToken.None);
        bResult.Rows.Should().ContainSingle();
        bResult.Rows[0].TokensIn.Should().Be(999, "tenant B sees only its own row");
    }

    // ──────────────────── Grouping + reconciliation (real GROUP BY) ────────────────────

    [Test]
    public async Task GetUsage_GroupBy_ReconcilesToUngrouped_OnRealPostgres_NullKeyPreserved()
    {
        var tenant = Guid.NewGuid();
        var schema = TenantNaming.SchemaName(tenant);
        await new EfTenantDbMigrator().MigrateTenantAppAsync(CsFor(schema));

        var day = new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc);
        await SeedDailyAsync(schema, tenant, day, "anthropic", tokensIn: 100, cost: 1.0m, ws: 2, ad: 3);
        await SeedDailyAsync(schema, tenant, day, "openai", tokensIn: 200, cost: 2.0m, ws: 1, ad: 1);
        await SeedDailyAsync(schema, tenant, day, provider: null, tokensIn: 0, cost: 0m, ws: 5, ad: 0);

        var factory = new SchemaRoutingFactory(_baseConnectionString).Map(tenant, schema);
        var service = new TenantAnalyticsService(factory, NullLogger<TenantAnalyticsService>.Instance);
        var window = AnalyticsWindow.Resolve(
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), "day");

        var ungrouped = await service.GetUsageAsync(tenant, window, null, CancellationToken.None);
        var grouped = await service.GetUsageAsync(tenant, window, AnalyticsDimension.Provider, CancellationToken.None);

        grouped.Rows.Should().HaveCount(3);
        grouped.Rows.Should().Contain(r => r.Key == null, "the NULL-provider bucket is surfaced, never dropped");
        grouped.Rows.Sum(r => r.TokensIn).Should().Be(ungrouped.Rows.Single().TokensIn);
        grouped.Rows.Sum(r => r.WorkflowsStarted).Should().Be(ungrouped.Rows.Single().WorkflowsStarted);
        grouped.Rows.Sum(r => r.CostUsd).Should().Be(ungrouped.Rows.Single().CostUsd);

        var breakdown = await service.GetBreakdownAsync(
            tenant, window, AnalyticsDimension.Provider, AnalyticsMetric.Tokens, 10, CancellationToken.None);
        breakdown.Rows[0].Key.Should().Be("openai", "openai has the most tokens (200 > 100 > 0)");
    }

    // ─────────────────── Range clamp + UTC window (real timestamptz) ───────────────────

    [Test]
    public async Task GetUsage_ClampsWindowTo365Days_AndSelectsUtcBuckets_OnRealTimestamptz()
    {
        var tenant = Guid.NewGuid();
        var schema = TenantNaming.SchemaName(tenant);
        await new EfTenantDbMigrator().MigrateTenantAppAsync(CsFor(schema));

        var to = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var recent = to.AddDays(-10);              // inside the clamped 365-day window
        var ancient = to.AddDays(-400);            // outside — must be truncated away
        await SeedDailyAsync(schema, tenant, recent, "anthropic", tokensIn: 50, cost: 0.5m);
        await SeedDailyAsync(schema, tenant, ancient, "anthropic", tokensIn: 77, cost: 0.7m);

        var factory = new SchemaRoutingFactory(_baseConnectionString).Map(tenant, schema);
        var service = new TenantAnalyticsService(factory, NullLogger<TenantAnalyticsService>.Instance);

        // Request a 400-day window → clamps to the most-recent 365 days.
        var window = AnalyticsWindow.Resolve(ancient, to, "day");
        window.From.Should().Be(to.AddDays(-365));
        var res = await service.GetUsageAsync(tenant, window, null, CancellationToken.None);

        res.Rows.Should().ContainSingle("the 400-day-old row is truncated out of the effective window");
        res.Rows[0].TokensIn.Should().Be(50);

        // UTC binding: a Local-kind `from` at the same instant selects identically.
        var localWindow = AnalyticsWindow.Resolve(
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime(), to, "day");
        var localRes = await service.GetUsageAsync(tenant, localWindow, null, CancellationToken.None);
        localRes.Rows.Should().ContainSingle();
        localRes.Rows[0].TokensIn.Should().Be(50, "a non-UTC-offset `from` selects the same bucket as its UTC equivalent");
    }

    // ──────────────────────────────── Helpers ────────────────────────────────

    private async Task SeedDailyAsync(
        string schema, Guid tenantId, DateTime day, string? provider,
        long tokensIn = 0, decimal cost = 0m, long ws = 0, long ad = 0)
    {
        var opts = new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(CsFor(schema)).Options;
        await using var ctx = new TenantDbContext(opts, tenantId);
        ctx.AnalyticsUsageDaily.Add(new AnalyticsUsageDaily
        {
            Id = Guid.NewGuid(),
            Day = day,
            Provider = provider,
            CostBasis = CostBasis.Platform,
            TokensIn = tokensIn,
            TokensOut = 0,
            CostUsd = cost,
            PlatformBilledUsd = cost,
            WorkflowsStarted = ws,
            WorkflowsCompleted = 0,
            WorkflowsFailed = 0,
            AgentDispatches = ad,
            ComputedAt = DateTime.UtcNow,
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
}
