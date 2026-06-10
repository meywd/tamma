using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Respawn;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Providers;

/// <summary>
/// Namespace-scoped Postgres fixture for the provider health / chain
/// integration tests. Independent of the shared
/// <see cref="Infrastructure.ApiTestFixture"/> so that multiple
/// <see cref="SetUpFixtureAttribute"/> instances don't try to own the same
/// static container at once.
/// </summary>
[SetUpFixture]
public class ProvidersSetUpFixture
{
    public static PostgreSqlContainer Postgres { get; private set; } = null!;
    /// <summary>
    /// Story 28-1 PR D — second Postgres container for tenant-resident
    /// schema (provider_health, provider_diagnostics, ...).
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
            .WithDatabase("tamma_providers_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        TenantPostgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tamma_providers_tenant_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();

        await Task.WhenAll(Postgres.StartAsync(), TenantPostgres.StartAsync());

        // Both migration baselines apply on bare Postgres — gen_random_uuid()
        // is a pg_catalog builtin since PG13; no extension bootstrap needed.

        // Environment variables have highest precedence in the default
        // configuration chain, so set the connection string there rather
        // than relying on ConfigureAppConfiguration which Program.cs happens
        // to read before our overrides can layer on top.
        // Phase-3: point both DefaultConnection and TammaDb at our container.
        // Clearing TammaDb would let appsettings.json's stale localhost
        // default win. Story 28-1 PR D: TammaAppDb points at the tenant
        // container so the tenant DbContext factory hits the moved tables.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection", Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__TammaDb", Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__TammaAppDb", TenantPostgres.GetConnectionString());
        Environment.SetEnvironmentVariable("OpenSearch__Enabled", "false");
        // Intentionally leave Jwt:Secret unset so Program.cs takes the
        // Development-mode branch that registers permissive policies.
        Environment.SetEnvironmentVariable("Jwt__Secret", null);
        Environment.SetEnvironmentVariable("Jwt__Issuer", null);
        Environment.SetEnvironmentVariable("Jwt__Audience", null);

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.DisableAlertHostedServices();
            });

        // Force service resolution so Program.cs applies migrations.
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        await db.Database.MigrateAsync();

        // Story 28-1 PR D — apply tenant migrations to the tenant container.
        await ApplyTenantMigrationsAsync(TenantPostgres.GetConnectionString());

        await using var conn = new Npgsql.NpgsqlConnection(Postgres.GetConnectionString());
        await conn.OpenAsync();
        _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            TablesToIgnore = new[] { new Respawn.Graph.Table("__ControlPlaneMigrationsHistory") },
            SchemasToInclude = new[] { "public" },
        });

        await using var tenantConn = new Npgsql.NpgsqlConnection(TenantPostgres.GetConnectionString());
        await tenantConn.OpenAsync();
        _tenantRespawner = await Respawner.CreateAsync(tenantConn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            TablesToIgnore = new[] { new Respawn.Graph.Table("__TenantMigrationsHistory") },
            SchemasToInclude = new[] { "public" },
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

        // Restore the env vars to the root ApiTestFixture's container so
        // sibling-namespace tests running after us can still resolve their
        // connection string. All three Phase-3 keys must be restored —
        // Program.cs reads TammaDb first, so leaving it null would let
        // appsettings.json's stale localhost default take over.
        if (ApiTestFixture.Postgres is not null)
        {
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__DefaultConnection",
                ApiTestFixture.Postgres.GetConnectionString());
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__TammaDb",
                ApiTestFixture.Postgres.GetConnectionString());
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__TammaAppDb",
                ApiTestFixture.TenantPostgres?.GetConnectionString() ?? "");
        }
    }

    public static async Task ResetDatabaseAsync()
    {
        await using var conn = new Npgsql.NpgsqlConnection(Postgres.GetConnectionString());
        await conn.OpenAsync();
        await _respawner.ResetAsync(conn);

        await using var tenantConn = new Npgsql.NpgsqlConnection(TenantPostgres.GetConnectionString());
        await tenantConn.OpenAsync();
        await _tenantRespawner.ResetAsync(tenantConn);

        // Phase 3 — Respawner wiped plans + tenant_databases; placement
        // needs both back before any test provisions a tenant.
        await TestTenantProvisioning.ReseedPoolAsync(
            Factory.Services, Postgres.GetConnectionString());
    }

    /// <summary>
    /// Phase 3 — provision a test tenant through the unified pipeline so
    /// the LRU resolver can reach its tenant data. The tenants row must
    /// exist before calling.
    /// </summary>
    public static Task ProvisionTenantAsync(Guid tenantId) =>
        TestTenantProvisioning.ProvisionAsync(Factory.Services, tenantId);

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
