using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Respawn;
using Tamma.Data;
using Testcontainers.PostgreSql;

// NOTE: the namespace is intentionally the root test namespace so that
// the [SetUpFixture] below fires for every test fixture in the assembly,
// regardless of sub-namespace (e.g. Tamma.Api.Tests.GitHub).
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

        // WebApplication.CreateBuilder(args) in Program.cs reads
        // builder.Configuration BEFORE WebApplicationFactory's
        // ConfigureAppConfiguration callbacks get a chance to override —
        // so in-memory config is invisible to GetConnectionString(). Using
        // env vars works because ASP.NET Core's default config providers
        // already include them. This is the documented workaround for the
        // "minimal hosting model + WebApplicationFactory" config-ordering
        // gotcha.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection", Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("OpenSearch__Enabled", "false");
        Environment.SetEnvironmentVariable(
            "Jwt__Secret", "test-secret-at-least-32-characters-long-for-hmac-signing");
        Environment.SetEnvironmentVariable("Jwt__Issuer", "tamma-test");
        Environment.SetEnvironmentVariable("Jwt__Audience", "tamma-api-test");

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = Postgres.GetConnectionString(),
                        ["OpenSearch:Enabled"] = "false",
                        ["Jwt:Secret"] = "test-secret-at-least-32-characters-long-for-hmac-signing",
                        ["Jwt:Issuer"] = "tamma-test",
                        ["Jwt:Audience"] = "tamma-api-test"
                    });
                });
            });

        // Enable the uuid-ossp extension — some legacy mentorship entity
        // columns reference uuid_generate_v4(). Production dev DBs get this
        // pre-seeded; the ephemeral test container needs it created first.
        await using (var extConn = new Npgsql.NpgsqlConnection(Postgres.GetConnectionString()))
        {
            await extConn.OpenAsync();
            await using var extCmd = extConn.CreateCommand();
            extCmd.CommandText = "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";";
            await extCmd.ExecuteNonQueryAsync();
        }

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
