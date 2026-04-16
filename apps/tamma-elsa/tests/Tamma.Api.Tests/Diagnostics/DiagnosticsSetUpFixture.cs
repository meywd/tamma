using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Npgsql;
using Respawn;
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
    public static WebApplicationFactory<Program> Factory { get; private set; } = null!;
    private static Respawner _respawner = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        Postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tamma_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();

        await Postgres.StartAsync();

        // Some CI hosts race the container readiness; wait for it to accept
        // connections before downstream components try to migrate.
        await WaitUntilReadyAsync(Postgres.GetConnectionString());

        // Set env vars BEFORE the factory builds so they win over
        // appsettings.Development.json (which hard-codes a dev DB).
        //
        // Deliberately leave Jwt:Secret empty in Development → Program.cs
        // wires the permissive AllowAnonymous auth policy, bypassing the
        // RequireAuthorization policies on the diagnostics endpoints.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection", Postgres.GetConnectionString());
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
                        ["OpenSearch:Enabled"] = "false",
                        ["Jwt:Secret"] = null
                    });
                });
            });

        // Some migrations depend on uuid-ossp / pgcrypto. Ensure both are
        // available before EF applies the InitialSchema migration.
        await using (var bootstrapConn = new NpgsqlConnection(Postgres.GetConnectionString()))
        {
            await bootstrapConn.OpenAsync();
            await using var bootstrapCmd = bootstrapConn.CreateCommand();
            bootstrapCmd.CommandText =
                "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";" +
                "CREATE EXTENSION IF NOT EXISTS \"pgcrypto\";";
            await bootstrapCmd.ExecuteNonQueryAsync();
        }

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(Postgres.GetConnectionString());
        await conn.OpenAsync();
        _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            TablesToIgnore = new[] { new Respawn.Graph.Table("__TammaMigrationsHistory") },
            SchemasToInclude = new[] { "public" }
        });
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        Factory?.Dispose();
        if (Postgres is not null)
            await Postgres.DisposeAsync();

        // Restore env var to the root ApiTestFixture's container so tests in
        // sibling namespaces running after us can resolve their connection.
        if (ApiTestFixture.Postgres is not null)
        {
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__DefaultConnection",
                ApiTestFixture.Postgres.GetConnectionString());
        }
    }

    /// <summary>Reset tenant-scoped data to a clean state between tests.</summary>
    public static async Task ResetDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(Postgres.GetConnectionString());
        await conn.OpenAsync();
        await _respawner.ResetAsync(conn);
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
