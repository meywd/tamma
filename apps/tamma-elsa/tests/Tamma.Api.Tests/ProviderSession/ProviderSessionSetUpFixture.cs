using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Npgsql;
using Respawn;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.ProviderSession;

/// <summary>
/// Namespace-scoped <see cref="SetUpFixtureAttribute"/> booting a dedicated
/// Postgres container for provider-session integration tests. Mirrors the
/// pattern used by <see cref="Diagnostics.DiagnosticsSetUpFixture"/> and
/// <see cref="Providers.ProvidersSetUpFixture"/> so each test namespace keeps
/// its own hermetic container + WebApplicationFactory.
/// </summary>
[SetUpFixture]
public class ProviderSessionSetUpFixture
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
            .WithDatabase("tamma_session_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        TenantPostgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tamma_session_tenant_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();

        await Task.WhenAll(Postgres.StartAsync(), TenantPostgres.StartAsync());
        await WaitUntilReadyAsync(Postgres.GetConnectionString());
        await WaitUntilReadyAsync(TenantPostgres.GetConnectionString());

        // Both migration baselines apply on bare Postgres — gen_random_uuid()
        // is a pg_catalog builtin since PG13; no extension bootstrap needed.

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
        Environment.SetEnvironmentVariable("Jwt__Secret", null);
        Environment.SetEnvironmentVariable("Jwt__Issuer", null);
        Environment.SetEnvironmentVariable("Jwt__Audience", null);

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.DisableAlertHostedServices();
            });

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        await db.Database.MigrateAsync();

        // Story 28-1 PR D — apply tenant migrations to the tenant container.
        await ApplyTenantMigrationsAsync(TenantPostgres.GetConnectionString());

        await using var conn = new NpgsqlConnection(Postgres.GetConnectionString());
        await conn.OpenAsync();
        _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            TablesToIgnore = new[] { new Respawn.Graph.Table("__ControlPlaneMigrationsHistory") },
            SchemasToInclude = new[] { "public" },
        });

        await using var tenantConn = new NpgsqlConnection(TenantPostgres.GetConnectionString());
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

        // Restore the shared ApiTestFixture's connection strings (all three
        // Phase-3 keys) so sibling namespaces that run after us can still
        // resolve their database against the parent container.
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
        await using var conn = new NpgsqlConnection(Postgres.GetConnectionString());
        await conn.OpenAsync();
        await _respawner.ResetAsync(conn);

        await using var tenantConn = new NpgsqlConnection(TenantPostgres.GetConnectionString());
        await tenantConn.OpenAsync();
        await _tenantRespawner.ResetAsync(tenantConn);
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

    private static async Task WaitUntilReadyAsync(string connectionString)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(500);
            }
        }
        throw new InvalidOperationException(
            $"Postgres did not become ready within 30s. Last error: {last?.Message}", last);
    }
}
