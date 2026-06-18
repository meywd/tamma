using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-1 (AC3) — applies the ControlPlane migration bundle to a clean
/// Postgres testcontainer and asserts the billing schema landed: the two new
/// tables, the BillingMode CHECK constraint, the unique tenant index, the
/// partial-unique Stripe-customer-id index, the unique slug index, and that
/// the migration rolls back cleanly (down drops both tables). Raw Npgsql
/// introspection — mirrors the existing migration suites.
/// </summary>
[TestFixture]
public class BillingMigrationTests
{
    private const string ThisMigration = "20260618212532_AddBillingCustomerAndPlanPrices";

    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("billing_migration_test")
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
    public async Task Migration_Creates_The_Two_Billing_Tables()
    {
        await using var conn = await OpenAsync();
        var tables = await QueryStringsAsync(conn,
            "SELECT table_name FROM information_schema.tables WHERE table_schema='public';");

        tables.Should().Contain("billing_customers");
        tables.Should().Contain("billing_plan_prices");
    }

    [Test]
    public async Task Migration_Creates_The_BillingMode_Check_Constraint()
    {
        await using var conn = await OpenAsync();
        var checks = await QueryStringsAsync(conn,
            "SELECT conname FROM pg_constraint WHERE contype='c';");

        checks.Should().Contain("ck_billing_customers_mode");
    }

    [Test]
    public async Task Migration_Creates_The_Unique_TenantId_Index()
    {
        await using var conn = await OpenAsync();
        var def = await ScalarAsync(conn,
            "SELECT indexdef FROM pg_indexes WHERE indexname='UX_billing_customers_TenantId';");

        def.Should().NotBeNull();
        def!.Should().Contain("UNIQUE");
        def.Should().Contain("TenantId");
    }

    [Test]
    public async Task Migration_Creates_The_Partial_Unique_StripeCustomerId_Index()
    {
        await using var conn = await OpenAsync();
        var def = await ScalarAsync(conn,
            "SELECT indexdef FROM pg_indexes WHERE indexname='UX_billing_customers_StripeCustomerId';");

        def.Should().NotBeNull();
        def!.Should().Contain("UNIQUE");
        def.Should().Contain("StripeCustomerId");
        def.Should().Contain("IS NOT NULL", "the index is partial — only acked customers");
    }

    [Test]
    public async Task Migration_Creates_The_Unique_PlanSlug_Index()
    {
        await using var conn = await OpenAsync();
        var def = await ScalarAsync(conn,
            "SELECT indexdef FROM pg_indexes WHERE indexname='UX_billing_plan_prices_PlanSlug';");

        def.Should().NotBeNull();
        def!.Should().Contain("UNIQUE");
        def.Should().Contain("PlanSlug");
    }

    [Test]
    public async Task Migration_Down_Drops_Both_Billing_Tables_Then_Up_Restores()
    {
        // Roll the schema back to the migration just before ours, then forward
        // again — proves the down() is correct and the migration is reversible.
        await using (var down = NewContext())
        {
            await down.GetService<IMigrator>()
                .MigrateAsync("20260618192337_PlanPriceBookCatalog");
        }

        await using (var conn = await OpenAsync())
        {
            var tables = await QueryStringsAsync(conn,
                "SELECT table_name FROM information_schema.tables WHERE table_schema='public';");
            tables.Should().NotContain("billing_customers");
            tables.Should().NotContain("billing_plan_prices");
        }

        // Forward again so the container is left in the fully-migrated state.
        await using (var up = NewContext())
        {
            await up.GetService<IMigrator>().MigrateAsync(ThisMigration);
        }

        await using (var conn = await OpenAsync())
        {
            var tables = await QueryStringsAsync(conn,
                "SELECT table_name FROM information_schema.tables WHERE table_schema='public';");
            tables.Should().Contain("billing_customers");
            tables.Should().Contain("billing_plan_prices");
        }
    }
}
