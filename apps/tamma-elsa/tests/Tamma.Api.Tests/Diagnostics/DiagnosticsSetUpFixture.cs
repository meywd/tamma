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

namespace Tamma.Api.Tests.Diagnostics;

/// <summary>
/// Namespace-scoped <see cref="SetUpFixtureAttribute"/> that boots a Postgres
/// test container and a <see cref="WebApplicationFactory{TEntryPoint}"/> for
/// the diagnostics integration tests.
/// </summary>
/// <remarks>
/// <para>
/// We mirror the shared <c>ApiTestFixture</c> rather than depend on it so
/// the diagnostics suite stays hermetic — NUnit's <c>[SetUpFixture]</c> in a
/// different namespace does not trigger a foreign fixture's OneTimeSetUp.
/// </para>
/// <para>
/// Tests in this namespace access the fixture via
/// <see cref="DiagnosticsTestHarness"/>, which wraps <see cref="Factory"/>
/// with <c>ConfigureTestServices</c> to add the diagnostics DI registrations.
/// </para>
/// </remarks>
[SetUpFixture]
public class DiagnosticsSetUpFixture
{
    public static PostgreSqlContainer Postgres { get; private set; } = null!;
    /// <summary>
    /// Story 28-1 PR D — second Postgres container for tenant-resident
    /// schema (provider_diagnostics, agent_configs, ...). Mirrors the
    /// shape of <see cref="ApiTestFixture.TenantPostgres"/>.
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

        // Some CI hosts race the container readiness; wait for it to accept
        // connections before downstream components try to migrate.
        await WaitUntilReadyAsync(Postgres.GetConnectionString());
        await WaitUntilReadyAsync(TenantPostgres.GetConnectionString());

        // Set env vars BEFORE the factory builds so they win over
        // appsettings.Development.json (which hard-codes a dev DB).
        //
        // Deliberately leave Jwt:Secret empty in Development → Program.cs
        // wires the permissive AllowAnonymous auth policy, bypassing the
        // RequireAuthorization policies on the diagnostics endpoints.
        // Phase-3: point BOTH DefaultConnection and TammaDb at our container.
        // Program.cs reads TammaDb first then falls back to DefaultConnection;
        // clearing TammaDb would let appsettings.json's stale localhost
        // default take over (empty password → auth failure).
        // Story 28-1 PR D: TammaAppDb now points at the tenant DB so the
        // ITenantDbContextFactory hits the moved tables (agent_configs,
        // provider_diagnostics, ...) physically.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection", Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__TammaDb", Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__TammaAppDb", TenantPostgres.GetConnectionString());
        Environment.SetEnvironmentVariable("OpenSearch__Enabled", "false");
        Environment.SetEnvironmentVariable("Jwt__Secret", null);

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    // Belt-and-braces override; env vars already win, but this
                    // keeps the test rig robust to future appsettings merges.
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = Postgres.GetConnectionString(),
                        ["ConnectionStrings:TammaAppDb"] = TenantPostgres.GetConnectionString(),
                        ["OpenSearch:Enabled"] = "false",
                        ["Jwt:Secret"] = null
                    });
                });
                builder.DisableAlertHostedServices();
            });

        // Both migration baselines apply on bare Postgres — gen_random_uuid()
        // is a pg_catalog builtin since PG13; no extension bootstrap needed.
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
            SchemasToInclude = new[] { "public" }
        });

        await using var tenantConn = new NpgsqlConnection(TenantPostgres.GetConnectionString());
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

        // Restore env vars to the root ApiTestFixture's container so tests in
        // sibling namespaces running after us can resolve their connection.
        // Phase-3 added TammaDb / TammaAppDb to the lookup chain — restore
        // those too so appsettings.json's stale localhost default doesn't
        // win when Program.cs reads TammaDb first.
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

    /// <summary>Reset tenant-scoped data to a clean state between tests.</summary>
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
