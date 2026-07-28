using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Providers;

/// <summary>
/// Story 46-1 (AC1/AC9 test 15) — pin the <c>provider_settings</c> migration
/// against a real Postgres instance (EF-InMemory enforces neither CHECK
/// constraints nor NULLS NOT DISTINCT): the scope/XOR CHECKs, the non-empty
/// model CHECK, the NULLS-NOT-DISTINCT unique principal index — and the
/// wipe-survival property the migration exists for: re-running the whole
/// control-plane migration graph after the Epic 19 startup wipe (which drops
/// <c>__ControlPlaneMigrationsHistory</c> but deliberately NOT
/// <c>provider_settings</c>) must neither 42P07 nor lose the saved rows.
///
/// <para>Mirrors <c>PromptOverridesPrincipalXorMigrationTests</c> (the
/// Testcontainers migration-test convention), on the CP graph instead of the
/// tenant graph.</para>
/// </summary>
[TestFixture]
public class ProviderSettingsMigrationTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("provider_settings_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        await MigrateControlPlaneAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _postgres.DisposeAsync();
    }

    [SetUp]
    public async Task ClearTable()
    {
        await ExecAsync("TRUNCATE TABLE provider_settings;");
    }

    private async Task MigrateControlPlaneAsync()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(_connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ControlPlaneMigrationsHistory"));
        ControlPlaneDbContext.ConfigureControlPlaneWarnings(options);
        await using var db = new ControlPlaneDbContext(options.Options);
        await db.Database.MigrateAsync();
    }

    private async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Insert(string tenantExpr, string userExpr, string scope,
        string providerKey, string modelExpr) =>
        $"""
        INSERT INTO provider_settings ("TenantId", "UserId", "Scope", "ProviderKey", "DefaultModel")
        VALUES ({tenantExpr}, {userExpr}, '{scope}', '{providerKey}', {modelExpr});
        """;

    // ── row kinds accepted ──────────────────────────────────────────────────

    [Test]
    public async Task PlatformRow_BothPrincipalsNull_Accepted()
    {
        await ExecAsync(Insert("NULL", "NULL", "platform", "openai", "'gpt-4o'"));
    }

    [Test]
    public async Task TenantRow_Accepted()
    {
        await ExecAsync(Insert("@tid", "NULL", "principal", "openai", "'gpt-4o-mini'"),
            ("tid", Guid.NewGuid()));
    }

    [Test]
    public async Task UserRow_Accepted()
    {
        await ExecAsync(Insert("NULL", "@uid", "principal", "anthropic", "'claude-opus-4-7'"),
            ("uid", Guid.NewGuid()));
    }

    [Test]
    public async Task FlagOnlyPlatformRow_NullModel_Accepted()
    {
        await ExecAsync(
            """
            INSERT INTO provider_settings ("TenantId", "UserId", "Scope", "ProviderKey", "DefaultModel", "Enabled")
            VALUES (NULL, NULL, 'platform', 'groq', NULL, FALSE);
            """);
    }

    // ── CHECK constraints fail closed ──────────────────────────────────────

    [Test]
    public async Task BothPrincipalsSet_Rejected()
    {
        var act = () => ExecAsync(Insert("@tid", "@uid", "principal", "openai", "'m'"),
            ("tid", Guid.NewGuid()), ("uid", Guid.NewGuid()));

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23514", "the XOR/scope CHECK rejects double principals");
    }

    [Test]
    public async Task PlatformScope_WithAPrincipalId_Rejected()
    {
        var act = () => ExecAsync(Insert("@tid", "NULL", "platform", "openai", "'m'"),
            ("tid", Guid.NewGuid()));

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23514", "the scope CHECK ties 'platform' to all-null principals");
    }

    [Test]
    public async Task PrincipalScope_WithNoPrincipalId_Rejected()
    {
        var act = () => ExecAsync(Insert("NULL", "NULL", "principal", "openai", "'m'"));

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23514");
    }

    [Test]
    public async Task EmptyStringModel_Rejected()
    {
        var act = () => ExecAsync(Insert("NULL", "NULL", "platform", "openai", "''"));

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23514",
                "a stored model is never the empty-string sentinel (config's \"\" keeps that meaning)");
    }

    // ── NULLS NOT DISTINCT uniqueness ──────────────────────────────────────

    [Test]
    public async Task DuplicatePlatformRow_RejectedDespiteNullPrincipals()
    {
        await ExecAsync(Insert("NULL", "NULL", "platform", "openai", "'gpt-4o'"));
        var act = () => ExecAsync(Insert("NULL", "NULL", "platform", "openai", "'gpt-4o-mini'"));

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23505",
                "NULLS NOT DISTINCT collapses the all-null platform principal to one row per provider");
    }

    [Test]
    public async Task DuplicateTenantRow_Rejected_ButOtherTenantsUnaffected()
    {
        var tid = Guid.NewGuid();
        await ExecAsync(Insert("@tid", "NULL", "principal", "openai", "'m1'"), ("tid", tid));

        var dup = () => ExecAsync(Insert("@tid", "NULL", "principal", "openai", "'m2'"), ("tid", tid));
        (await dup.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");

        // A different tenant's row for the same provider is fine.
        await ExecAsync(Insert("@tid", "NULL", "principal", "openai", "'m3'"), ("tid", Guid.NewGuid()));
    }

    // ── the wipe-survival property ──────────────────────────────────────────

    [Test]
    public async Task Epic19Wipe_ThenRemigrate_TableSurvivesWithRows()
    {
        // A saved selection…
        await ExecAsync(Insert("NULL", "NULL", "platform", "anthropic", "'claude-opus-4-7'"));

        // …then the Epic 19 wipe re-runs the whole CP migration graph from a
        // dropped history table while provider_settings (deliberately NOT on
        // the DROP list) still exists. For every OTHER table the wipe dropped
        // them first, so only THIS migration re-executes against an existing
        // table — its DDL must be IF-NOT-EXISTS idempotent (no 42P07) and
        // must not touch the surviving rows. Re-run exactly the migration's
        // SQL the way the re-migrate would:
        await ExecAsync("""
            CREATE TABLE IF NOT EXISTS provider_settings (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "TenantId" uuid NULL,
                "UserId" uuid NULL,
                "Scope" character varying(16) NOT NULL,
                "ProviderKey" character varying(100) NOT NULL,
                "DefaultModel" character varying(256) NULL,
                "Enabled" boolean NOT NULL DEFAULT TRUE,
                "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedBy" uuid NULL,
                CONSTRAINT "PK_provider_settings" PRIMARY KEY ("Id")
            );
            CREATE INDEX IF NOT EXISTS "IX_provider_settings_ProviderKey"
                ON provider_settings ("ProviderKey");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_provider_settings_TenantId_UserId_ProviderKey"
                ON provider_settings ("TenantId", "UserId", "ProviderKey")
                NULLS NOT DISTINCT;
            """);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """SELECT "DefaultModel" FROM provider_settings WHERE "ProviderKey" = 'anthropic';""",
            conn);
        (await cmd.ExecuteScalarAsync()).Should().Be("claude-opus-4-7",
            "a model picked in the UI must survive the wipe-and-remigrate deploy cycle");
    }
}
