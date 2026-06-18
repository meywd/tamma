using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-1 (AC7, AC8) — applies the ControlPlane migration bundle to a clean
/// Postgres testcontainer and asserts the price-book schema landed: the three
/// new tables, the new plan columns, the CHECK constraints, the partial unique
/// "one active per slug" index, the <c>text</c> metric-key column, and that the
/// seeded plan rows backfill to <c>Status='active'</c>/<c>Version=1</c>. Raw
/// Npgsql introspection (no Dapper) — mirrors the existing migration suites.
/// </summary>
[TestFixture]
public class PlanCatalogMigrationTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("plan_migration_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        await using var ctx = NewContext();
        await ctx.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<HashSet<string>> QueryStringsAsync(NpgsqlConnection conn, string sql)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0)) result.Add(reader.GetString(0));
        }
        return result;
    }

    private async Task<string?> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? null : v.ToString();
    }

    [Test]
    public async Task Migration_Creates_The_Three_PriceBook_Tables()
    {
        await using var conn = await OpenAsync();
        var tables = await QueryStringsAsync(conn,
            "SELECT table_name FROM information_schema.tables WHERE table_schema='public';");

        tables.Should().Contain("plan_features");
        tables.Should().Contain("plan_entitlements");
        tables.Should().Contain("plan_prices");
    }

    [Test]
    public async Task Migration_Adds_The_New_Plan_Columns()
    {
        await using var conn = await OpenAsync();
        var cols = await QueryStringsAsync(conn,
            "SELECT column_name FROM information_schema.columns WHERE table_name='plans';");

        cols.Should().Contain(new[] { "Version", "Status", "IsCustom", "BillingInterval", "SupersedesPlanId" });
    }

    [Test]
    public async Task Migration_Creates_The_Check_Constraints()
    {
        await using var conn = await OpenAsync();
        var checks = await QueryStringsAsync(conn,
            "SELECT conname FROM pg_constraint WHERE contype='c';");

        checks.Should().Contain("ck_plans_status");
        checks.Should().Contain("ck_plans_billing_interval");
        checks.Should().Contain("ck_plan_entitlements_period");
        checks.Should().Contain("ck_plan_entitlements_overage");
        checks.Should().Contain("ck_plan_prices_mode");
    }

    [Test]
    public async Task Migration_Creates_The_Partial_OneActivePerSlug_Index()
    {
        await using var conn = await OpenAsync();
        var def = await ScalarAsync(conn,
            "SELECT indexdef FROM pg_indexes WHERE indexname='UX_plans_OneActivePerSlug';");

        def.Should().NotBeNull();
        def!.Should().Contain("UNIQUE");
        def.Should().Contain("Status");
        def.Should().Contain("active", "the index is filtered WHERE Status = 'active'");
    }

    [Test]
    public async Task MetricKey_Column_Is_Text_Not_Integer()
    {
        await using var conn = await OpenAsync();
        var dataType = await ScalarAsync(conn,
            "SELECT data_type FROM information_schema.columns "
            + "WHERE table_name='plan_entitlements' AND column_name='MetricKey';");

        dataType.Should().Be("text", "the metric key persists as snake_case text, never the ordinal");
    }

    [Test]
    public async Task Seeded_Plans_Backfill_To_Active_Version_1()
    {
        await using (var ctx = NewContext())
        {
            await ctx.Database.ExecuteSqlRawAsync(
                "TRUNCATE plan_prices, plan_entitlements, plan_features, plans CASCADE;");
            await PlansSeeder.SeedAsync(ctx);
        }

        await using var verify = NewContext();
        var plans = await verify.Plans.AsNoTracking().ToListAsync();
        plans.Should().HaveCount(3);
        plans.Should().OnlyContain(p => p.Version == 1 && p.Status == "active");
    }
}
