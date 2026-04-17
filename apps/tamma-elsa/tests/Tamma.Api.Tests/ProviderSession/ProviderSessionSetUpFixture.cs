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
    public static WebApplicationFactory<Program> Factory { get; private set; } = null!;
    private static Respawner _respawner = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        Postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tamma_session_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();

        await Postgres.StartAsync();
        await WaitUntilReadyAsync(Postgres.GetConnectionString());

        // uuid-ossp + pgcrypto are used by earlier migrations — create up-front.
        await using (var bootstrap = new NpgsqlConnection(Postgres.GetConnectionString()))
        {
            await bootstrap.OpenAsync();
            await using var cmd = bootstrap.CreateCommand();
            cmd.CommandText =
                "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";" +
                "CREATE EXTENSION IF NOT EXISTS \"pgcrypto\";";
            await cmd.ExecuteNonQueryAsync();
        }

        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection", Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("OpenSearch__Enabled", "false");
        Environment.SetEnvironmentVariable("Jwt__Secret", null);
        Environment.SetEnvironmentVariable("Jwt__Issuer", null);
        Environment.SetEnvironmentVariable("Jwt__Audience", null);

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(Postgres.GetConnectionString());
        await conn.OpenAsync();
        _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            TablesToIgnore = new[] { new Respawn.Graph.Table("__TammaMigrationsHistory") },
            SchemasToInclude = new[] { "public" },
        });
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        Factory?.Dispose();
        if (Postgres is not null)
            await Postgres.DisposeAsync();

        // Restore the shared ApiTestFixture's connection string so sibling
        // namespaces that run after us can still resolve their database.
        if (ApiTestFixture.Postgres is not null)
        {
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__DefaultConnection",
                ApiTestFixture.Postgres.GetConnectionString());
        }
    }

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
