using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Respawn;
using Tamma.Api.Extensions;
using Tamma.Data;
using Testcontainers.PostgreSql;

// NOTE: This fixture is placed in the root Tamma.Api.Tests namespace (not
// Tamma.Api.Tests.Infrastructure) so that NUnit's [SetUpFixture] scope covers
// *all* test namespaces in this assembly — SetUpFixture applies only to the
// namespace it lives in and descendants. Sibling namespaces like
// Tamma.Api.Tests.Agents would otherwise not see the one-time DB container.
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

        // Install uuid-ossp extension required by migrations' uuid_generate_v4()
        // defaults. Production images ship with it pre-enabled; the alpine
        // test image requires an explicit CREATE EXTENSION.
        await using (var bootstrap = new Npgsql.NpgsqlConnection(Postgres.GetConnectionString()))
        {
            await bootstrap.OpenAsync();
            await using var cmd = bootstrap.CreateCommand();
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";";
            await cmd.ExecuteNonQueryAsync();
        }

        // Point the host at the Testcontainers-managed Postgres BEFORE the
        // WebApplication is built. WebApplication.CreateBuilder reads
        // environment variables early, so this wins over appsettings.json.
        // NB: Jwt:Secret is intentionally NOT set so Program.cs takes the
        // Development-mode permissive-auth branch (endpoints return 200
        // instead of 401). See Program.cs line ~227.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection",
            Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("OpenSearch__Enabled", "false");
        Environment.SetEnvironmentVariable("Jwt__Secret", null);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                // Keep in-memory config as a belt-and-braces override for any
                // consumer resolving IConfiguration inside ConfigureServices.
                // Jwt:Secret is deliberately absent so Program.cs takes the
                // Development-mode permissive-auth branch.
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = Postgres.GetConnectionString(),
                        ["OpenSearch:Enabled"] = "false",
                    });
                });
                // Agent-resolver stack is wired by the feature extension;
                // Program.cs intentionally defers to the parent host to call
                // this. Register it here so integration tests can exercise
                // the endpoints end-to-end.
                builder.ConfigureTestServices(services =>
                {
                    services.AddAgentResolverServices();
                });
            });

        // Force service resolution so Program.cs migrations run against the container.
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        await db.Database.MigrateAsync();

        await using var conn = new Npgsql.NpgsqlConnection(Postgres.GetConnectionString());
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
    }

    /// <summary>Call from <c>[SetUp]</c> in each test class to clear tenant-scoped data.</summary>
    public static async Task ResetDatabaseAsync()
    {
        await using var conn = new Npgsql.NpgsqlConnection(Postgres.GetConnectionString());
        await conn.OpenAsync();
        await _respawner.ResetAsync(conn);
    }

    public static HttpClient CreateClient() => Factory.CreateClient();
}
