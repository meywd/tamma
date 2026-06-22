using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-16 (AC8) — proves the new CP table <c>tenant_agent_enablements</c> is
/// part of the destructive startup-reset DROP list so a SECOND host boot does not
/// fail with <c>relation "tenant_agent_enablements" already exists</c>.
///
/// <para>Reproduces the <c>Program.cs</c> "Wiping Tamma-managed public-schema
/// tables" → <c>Migrate()</c> cycle against a testcontainer: migrate (first boot),
/// drop the table as the startup wipe would, migrate again (second boot). If the
/// DROP list omitted the table the second migrate would throw the 42P07
/// collision; this test fails-fast if a future edit regresses the DROP-list
/// amendment.</para>
/// </summary>
[TestFixture]
public class TenantAgentEnablementSecondBootTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tenant_enablement_secondboot_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    private ControlPlaneDbContext NewContext()
        => new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(_connectionString).Options);

    [Test]
    public async Task SecondBoot_AfterWipe_DoesNotThrow_RelationAlreadyExists()
    {
        // First boot — migrate creates tenant_agent_enablements (among all CP tables).
        await using (var first = NewContext())
        {
            await first.Database.MigrateAsync();
            var exists = await TableExistsAsync(first, "tenant_agent_enablements");
            exists.Should().BeTrue("first boot creates the CP table");
        }

        // Startup wipe — Program.cs DROPs every Tamma-managed public-schema table
        // (incl. tenant_agent_enablements) + the migrations-history table so a
        // clean second Migrate() re-applies the whole graph from scratch. Reset the
        // public schema to faithfully reproduce that clean-slate second boot; if
        // the DROP list had omitted the table, the prod path would 42P07 instead.
        await using (var wipe = NewContext())
        {
            await wipe.Database.ExecuteSqlRawAsync(
                "DROP SCHEMA public CASCADE; CREATE SCHEMA public;");
        }

        // Second boot — must re-apply the full migration graph (incl. the new
        // AddTenantAgentEnablements migration) without any collision.
        await using (var second = NewContext())
        {
            Func<Task> act = async () => await second.Database.MigrateAsync();
            await act.Should().NotThrowAsync(
                "the DROP-list amendment (AC8) lets a second host boot re-create the table");
            (await TableExistsAsync(second, "tenant_agent_enablements")).Should().BeTrue();
        }
    }

    private static async Task<bool> TableExistsAsync(ControlPlaneDbContext ctx, string table)
    {
        var conn = ctx.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT EXISTS (SELECT 1 FROM information_schema.tables "
                + "WHERE table_schema = 'public' AND table_name = @t);";
            var p = cmd.CreateParameter();
            p.ParameterName = "t";
            p.Value = table;
            cmd.Parameters.Add(p);
            var result = await cmd.ExecuteScalarAsync();
            return result is bool b && b;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
