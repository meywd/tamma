using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Npgsql;
using Respawn;
using Tamma.Data;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Tenancy;

/// <summary>
/// Namespace-scoped fixture that boots a dedicated Postgres container for
/// Phase-3 dual-connection-string + RLS integration tests. Matches the
/// pattern used by <c>DiagnosticsSetUpFixture</c> so the tenant-isolation
/// suite stays hermetic from the shared <see cref="ApiTestFixture"/>.
/// </summary>
/// <remarks>
/// <para>
/// This fixture sets up:
/// </para>
/// <list type="bullet">
///   <item><description>Admin connection (<c>ConnectionStrings:TammaDb</c>)
///     running as the superuser <c>tamma</c> — used for migrations and
///     cross-tenant admin paths.</description></item>
///   <item><description>App connection (<c>ConnectionStrings:TammaAppDb</c>)
///     running as <c>tamma_app</c> — the unprivileged role created by the
///     Phase-2 RLS migration. The password is forced to a known value
///     (<c>app_test_pw</c>) after migrations land so the connection
///     actually works.</description></item>
/// </list>
/// <para>
/// Tests reset the schema between cases via <see cref="ResetDatabaseAsync"/>
/// (Respawner) so one tenant's seeded rows don't leak into another test.
/// </para>
/// </remarks>
[SetUpFixture]
public class TenancySetUpFixture
{
    public const string AppRolePassword = "app_test_pw";

    public static PostgreSqlContainer Postgres { get; private set; } = null!;
    public static WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public static string AdminConnectionString { get; private set; } = null!;
    public static string AppConnectionString { get; private set; } = null!;

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
        await WaitUntilReadyAsync(Postgres.GetConnectionString());

        AdminConnectionString = Postgres.GetConnectionString();

        // Rewrite the admin connection string to derive the app connection
        // string against the same database but as the tamma_app role. The
        // Phase-2 migration creates the role with a placeholder password;
        // we re-ALTER it after migrations land.
        var adminBuilder = new NpgsqlConnectionStringBuilder(AdminConnectionString);
        var appBuilder = new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Username = "tamma_app",
            Password = AppRolePassword,
        };
        AppConnectionString = appBuilder.ToString();

        Environment.SetEnvironmentVariable(
            "ConnectionStrings__TammaDb", AdminConnectionString);
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__TammaAppDb", AppConnectionString);
        // Keep legacy key populated for code paths that still read it.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection", AdminConnectionString);
        Environment.SetEnvironmentVariable("OpenSearch__Enabled", "false");
        Environment.SetEnvironmentVariable("Jwt__Secret", null);

        // Extensions must be installed as superuser before migrations run.
        await using (var bootstrap = new NpgsqlConnection(AdminConnectionString))
        {
            await bootstrap.OpenAsync();
            await using var cmd = bootstrap.CreateCommand();
            cmd.CommandText =
                "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";" +
                "CREATE EXTENSION IF NOT EXISTS \"pgcrypto\";";
            await cmd.ExecuteNonQueryAsync();
        }

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development"));

        // Materialise the factory + run migrations via Program.cs.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
            await db.Database.MigrateAsync();
        }

        // After Phase-2 migration installed the tamma_app role with
        // 'changeme', reset the password so the app connection string can
        // actually bind. Also ensure the role is granted the necessary
        // default privileges on any tables that existed before the Phase-2
        // migration (the ALTER DEFAULT PRIVILEGES in the migration only
        // applies to tables created AFTER the statement runs).
        await using (var admin = new NpgsqlConnection(AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $@"
                ALTER ROLE tamma_app WITH PASSWORD '{AppRolePassword}';
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO tamma_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO tamma_app;
            ";
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var conn = new NpgsqlConnection(AdminConnectionString))
        {
            await conn.OpenAsync();
            _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
            {
                DbAdapter = DbAdapter.Postgres,
                TablesToIgnore = new[] { new Respawn.Graph.Table("__TammaMigrationsHistory") },
                SchemasToInclude = new[] { "public" }
            });
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        Factory?.Dispose();
        if (Postgres is not null)
            await Postgres.DisposeAsync();

        // Restore env var to the root ApiTestFixture so sibling suites can
        // resolve their connections afterwards.
        if (ApiTestFixture.Postgres is not null)
        {
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__DefaultConnection",
                ApiTestFixture.Postgres.GetConnectionString());
        }
        Environment.SetEnvironmentVariable("ConnectionStrings__TammaDb", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__TammaAppDb", null);
    }

    public static async Task ResetDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnectionString);
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
