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
/// Story 35-4 (AC1) — applies the ControlPlane migration bundle to a clean
/// Postgres testcontainer and asserts the <c>billing_subscriptions</c> schema
/// landed: the table, the status CHECK, the partial-unique
/// "one-non-terminal-per-tenant" index, the partial-unique Stripe-subscription-id
/// index, and clean down/up reversibility. Raw Npgsql introspection — mirrors
/// <see cref="BillingMigrationTests"/>.
/// </summary>
[TestFixture]
public class BillingSubscriptionMigrationTests
{
    private const string ThisMigration = "20260702193642_AddBillingSubscription";
    private const string PriorMigration = "20260702172018_BillingWebhookEvents";

    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("billing_subscription_migration_test")
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

    private static async Task<string?> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? null : v.ToString();
    }

    [Test]
    public async Task Migration_Creates_The_Subscriptions_Table_And_Status_Check()
    {
        await using var conn = await OpenAsync();

        var table = await ScalarAsync(conn,
            "SELECT table_name FROM information_schema.tables "
            + "WHERE table_schema='public' AND table_name='billing_subscriptions';");
        table.Should().Be("billing_subscriptions");

        var check = await ScalarAsync(conn,
            "SELECT conname FROM pg_constraint "
            + "WHERE contype='c' AND conname='ck_billing_subscriptions_status';");
        check.Should().Be("ck_billing_subscriptions_status");
    }

    [Test]
    public async Task Migration_Creates_The_Partial_Unique_OneNonTerminal_PerTenant_Index()
    {
        await using var conn = await OpenAsync();
        var def = await ScalarAsync(conn,
            "SELECT indexdef FROM pg_indexes "
            + "WHERE indexname='UX_billing_subscriptions_TenantId_NonTerminal';");

        def.Should().NotBeNull();
        def!.Should().Contain("UNIQUE");
        def.Should().Contain("TenantId");
        def.Should().Contain("canceled", "the index is partial — excludes terminal statuses");
        def.Should().Contain("incomplete_expired");
    }

    [Test]
    public async Task Migration_Creates_The_Partial_Unique_StripeSubscriptionId_Index()
    {
        await using var conn = await OpenAsync();
        var def = await ScalarAsync(conn,
            "SELECT indexdef FROM pg_indexes "
            + "WHERE indexname='UX_billing_subscriptions_StripeSubscriptionId';");

        def.Should().NotBeNull();
        def!.Should().Contain("UNIQUE");
        def.Should().Contain("StripeSubscriptionId");
        def.Should().Contain("IS NOT NULL", "the index is partial — only acked subscriptions");
    }

    [Test]
    public async Task PartialUnique_Rejects_Second_NonTerminal_But_Allows_Terminal_Plus_Active()
    {
        var tenantId = await SeedTenantAsync();

        // First non-terminal row — OK.
        await InsertSubscriptionAsync(tenantId, "sub_A", "active");

        // Second non-terminal row for the SAME tenant — rejected by the partial index.
        var act = async () => await InsertSubscriptionAsync(tenantId, "sub_B", "trialing");
        (await act.Should().ThrowAsync<PostgresException>())
            .Where(e => e.SqlState == PostgresErrorCodes.UniqueViolation);

        // A terminal row + a fresh non-terminal row coexist fine.
        await CancelSubscriptionAsync(tenantId, "sub_A");
        await InsertSubscriptionAsync(tenantId, "sub_C", "active");
    }

    [Test]
    public async Task Migration_Down_Drops_Then_Up_Restores()
    {
        await using (var down = NewContext())
        {
            await down.GetService<IMigrator>().MigrateAsync(PriorMigration);
        }
        await using (var conn = await OpenAsync())
        {
            var t = await ScalarAsync(conn,
                "SELECT table_name FROM information_schema.tables "
                + "WHERE table_schema='public' AND table_name='billing_subscriptions';");
            t.Should().BeNull();
        }

        await using (var up = NewContext())
        {
            await up.GetService<IMigrator>().MigrateAsync(ThisMigration);
        }
        await using (var conn = await OpenAsync())
        {
            var t = await ScalarAsync(conn,
                "SELECT table_name FROM information_schema.tables "
                + "WHERE table_schema='public' AND table_name='billing_subscriptions';");
            t.Should().Be("billing_subscriptions");
        }
    }

    private async Task<Guid> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO tenants (\"Id\", \"Name\", \"Slug\", \"Plan\", \"CreatedAt\", \"UpdatedAt\") "
            + "VALUES (@id, @name, @slug, 'free', now(), now());", conn);
        cmd.Parameters.AddWithValue("id", tenantId);
        cmd.Parameters.AddWithValue("name", $"t-{tenantId:N}");
        cmd.Parameters.AddWithValue("slug", $"s{tenantId:N}"[..12]);
        await cmd.ExecuteNonQueryAsync();
        return tenantId;
    }

    private async Task InsertSubscriptionAsync(Guid tenantId, string stripeSubId, string status)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO billing_subscriptions "
            + "(\"Id\", \"TenantId\", \"StripeSubscriptionId\", \"PlanSlug\", \"Status\", "
            + "\"CurrentPeriodStart\", \"CurrentPeriodEnd\", \"CancelAtPeriodEnd\", \"Seats\", "
            + "\"CreatedAt\", \"UpdatedAt\") "
            + "VALUES (gen_random_uuid(), @t, @sid, 'team', @st, now(), now(), false, 1, now(), now());",
            conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("sid", stripeSubId);
        cmd.Parameters.AddWithValue("st", status);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CancelSubscriptionAsync(Guid tenantId, string stripeSubId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE billing_subscriptions SET \"Status\"='canceled' "
            + "WHERE \"TenantId\"=@t AND \"StripeSubscriptionId\"=@sid;", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("sid", stripeSubId);
        await cmd.ExecuteNonQueryAsync();
    }
}
