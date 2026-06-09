using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Respawn;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Testcontainers.PostgreSql;

// NOTE: namespace is intentionally the root `Tamma.Api.Tests`, not
// `...Infrastructure`. NUnit's [SetUpFixture] applies to its declared
// namespace and sub-namespaces, so placing it at the root lets every test
// file anywhere under Tamma.Api.Tests share the container + migrations
// without needing a local delegate fixture.
namespace Tamma.Api.Tests;

/// <summary>
/// Shared integration-test fixture: boots a Postgres container once per test
/// assembly, runs EF migrations against it, and exposes a
/// <see cref="WebApplicationFactory{TEntryPoint}"/> pointed at that container.
///
/// Subclass and decorate tests with <c>[SetUp]</c> calling
/// <see cref="ResetDatabaseAsync"/> between tests for isolation.
/// </summary>
[SetUpFixture]
public class ApiTestFixture
{
    public static PostgreSqlContainer Postgres { get; private set; } = null!;
    /// <summary>
    /// Story 28-1 PR D — second Postgres container that holds the per-tenant
    /// schema (agent_configs, prompt_overrides, provider_health, ...).
    /// In production each tenant gets its own physical DB; in tests we use
    /// one shared tenant DB and let <c>TenantDbContextFactory</c>'s shared
    /// connection-string mode (<c>StubTenantConnectionResolver</c>) hand
    /// every tenant the same connection. The CP migration drops the moved
    /// tables from this fixture's CP DB; the tenant migration creates them
    /// here. Without this split, every test that exercises a moved entity
    /// hits "relation does not exist" because the moved tables only live
    /// on the tenant DB now.
    /// </summary>
    public static PostgreSqlContainer TenantPostgres { get; private set; } = null!;
    public static WebApplicationFactory<Program> Factory { get; private set; } = null!;
    private static Respawner _respawner = null!;
    private static Respawner _tenantRespawner = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        Postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tamma_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        TenantPostgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tamma_tenant_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();

        await Task.WhenAll(Postgres.StartAsync(), TenantPostgres.StartAsync());

        // Program.cs reads `builder.Configuration.GetConnectionString(...)` at
        // WebApplicationBuilder-composition time, BEFORE any callback passed to
        // WithWebHostBuilder.ConfigureAppConfiguration runs. That means an
        // in-memory override attached via ConfigureAppConfiguration is too
        // late. The reliable override for .NET 8 minimal APIs is an env var
        // (double-underscore path syntax), which is loaded automatically by
        // the default configuration sources during CreateBuilder.
        // Phase-3 added TammaDb / TammaAppDb as the primary lookup keys in
        // Program.cs. appsettings.json ships with a stale localhost default
        // for TammaDb (empty password), so clearing the env var lets the
        // appsettings layer take over and we fail to auth. Instead, point
        // CP keys at the CP container and TammaAppDb at the tenant container
        // so the per-tenant DbContext factory hits the tenant schema.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection", Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__TammaDb", Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__TammaAppDb", TenantPostgres.GetConnectionString());
        Environment.SetEnvironmentVariable("OpenSearch__Enabled", "false");

        // Intentionally DO NOT set Jwt__Secret here. Program.cs picks one of
        // three auth branches: real JWT (secret present), permissive dev
        // (secret empty + Development env), or hard-fail (empty + non-dev).
        // Tests use the permissive dev branch so they can exercise endpoints
        // behind RequireAuthorization without minting tokens. Clear any stale
        // value from earlier runs.
        Environment.SetEnvironmentVariable("Jwt__Secret", null);
        Environment.SetEnvironmentVariable("Jwt__Issuer", null);
        Environment.SetEnvironmentVariable("Jwt__Audience", null);

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                // PR #329 wired three always-on hosted services into
                // Program.cs (seeder + rule evaluator + dispatcher).
                // Gate them off for the shared fixture so 75 tests
                // don't each pay the per-host startup cost. Opt-in
                // tests (E2EAlertFlowTests, AlertRuleEndpointsIntegrationTests)
                // drive them via the public *Once / SeedAsync APIs.
                builder.DisableAlertHostedServices();
            });

        // The InitialSchema migration references uuid_generate_v4() (pre-existing
        // mentorship schema) which lives in the uuid-ossp extension. Enable it
        // before running migrations so the container image (stock postgres:17-alpine)
        // can execute the migration bundle. Same need on the tenant DB —
        // the tenant migration creates uuid + jsonb columns.
        await EnableExtensionsAsync(Postgres.GetConnectionString());
        await EnableExtensionsAsync(TenantPostgres.GetConnectionString());

        // Force service resolution so Program.cs migrations run against the container.
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        await db.Database.MigrateAsync();

        // Story 28-1 PR D — apply tenant migrations to the tenant container
        // so moved tables (agent_configs, provider_health, domain_events,
        // ...) physically exist before any test seeds them.
        await ApplyTenantMigrationsAsync(TenantPostgres.GetConnectionString());

        await using var conn = new Npgsql.NpgsqlConnection(Postgres.GetConnectionString());
        await conn.OpenAsync();
        _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            TablesToIgnore = new[] { new Respawn.Graph.Table("__ControlPlaneMigrationsHistory") },
            SchemasToInclude = new[] { "public" }
        });

        await using var tenantConn = new Npgsql.NpgsqlConnection(TenantPostgres.GetConnectionString());
        await tenantConn.OpenAsync();
        _tenantRespawner = await Respawner.CreateAsync(tenantConn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            TablesToIgnore = new[] { new Respawn.Graph.Table("__TenantMigrationsHistory") },
            SchemasToInclude = new[] { "public" }
        });
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        Factory?.Dispose();
        if (Postgres is not null)
            await Postgres.DisposeAsync();
        if (TenantPostgres is not null)
            await TenantPostgres.DisposeAsync();
    }

    /// <summary>Call from <c>[SetUp]</c> in each test class to clear tenant-scoped data.</summary>
    public static async Task ResetDatabaseAsync()
    {
        await using var conn = new Npgsql.NpgsqlConnection(Postgres.GetConnectionString());
        await conn.OpenAsync();
        await _respawner.ResetAsync(conn);

        await using var tenantConn = new Npgsql.NpgsqlConnection(TenantPostgres.GetConnectionString());
        await tenantConn.OpenAsync();
        await _tenantRespawner.ResetAsync(tenantConn);
    }

    public static HttpClient CreateClient() => Factory.CreateClient();

    private static async Task EnableExtensionsAsync(string connectionString)
    {
        await using var bootstrap = new Npgsql.NpgsqlConnection(connectionString);
        await bootstrap.OpenAsync();
        await using var cmd = bootstrap.CreateCommand();
        cmd.CommandText =
            "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";" +
            "CREATE EXTENSION IF NOT EXISTS \"pgcrypto\";";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ApplyTenantMigrationsAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory"))
            .Options;
        await using var ctx = new TenantDbContext(options);
        await ctx.Database.MigrateAsync();
    }
}
