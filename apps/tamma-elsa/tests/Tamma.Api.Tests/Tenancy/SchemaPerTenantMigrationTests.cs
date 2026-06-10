using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Tenancy;

/// <summary>
/// Unified-tenancy Phase 1 — the two-tenants-one-DB proof. The tenant's
/// schema is carried ONLY by the connection string's <c>Search Path</c> key:
/// <see cref="EfTenantDbMigrator.MigrateTenantAppAsync"/> must apply the
/// (collapsed) <c>InitialTenant</c> baseline into that schema, with the
/// <c>__TenantMigrationsHistory</c> table pinned to the SAME schema so each
/// tenant tracks its own applied set independently.
///
/// <para>The fixture spins a single Postgres 17 container and migrates two
/// distinct tenant schemas into the SAME database. Assertions:</para>
/// <list type="number">
///   <item>Both schemas carry their own <c>conventions</c> table AND their
///     own <c>__TenantMigrationsHistory</c>.</item>
///   <item>Re-running the migrator for an already-migrated schema is a
///     no-op (per-schema history), not a failure.</item>
///   <item>A row written through schema A's context is invisible through
///     schema B's context — <see cref="TenantDbContext"/> carries no EF
///     query filters (see <c>TammaModelConfiguration.ApplyTenantFilter</c>,
///     a deliberate no-op), so the ONLY isolation plane here is the
///     search_path schema.</item>
/// </list>
///
/// <para>No <c>Search Path</c> in the connection string → null schema →
/// the migrator behaves exactly as before Phase 1 (everything in
/// <c>public</c>); that path stays covered by the existing
/// <c>ConventionStoreMigrationTests</c> / <c>PromptOverridesPrincipalXorMigrationTests</c>.</para>
/// </summary>
[TestFixture]
public class SchemaPerTenantMigrationTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _baseConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("schema_per_tenant_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _baseConnectionString = _postgres.GetConnectionString();

        // The collapsed InitialTenant baseline only references
        // gen_random_uuid(), a pg_catalog builtin (Postgres 13+), so it
        // resolves even when search_path names ONLY the tenant schema —
        // no extension install is required here.
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _postgres.DisposeAsync();
    }

    [Test]
    public async Task MigrateTenantApp_AppliesIntoSearchPathSchema_TwoTenantsCoexistInOneDb()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var schemaA = TenantNaming.SchemaName(tenantA);
        var schemaB = TenantNaming.SchemaName(tenantB);

        string CsFor(string schema) =>
            new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = schema }
                .ConnectionString;

        var migrator = new EfTenantDbMigrator();
        await migrator.MigrateTenantAppAsync(CsFor(schemaA));
        await migrator.MigrateTenantAppAsync(CsFor(schemaB));
        // Idempotency: re-run for A must be a no-op (its own history table
        // already records the applied set), not a failure.
        await migrator.MigrateTenantAppAsync(CsFor(schemaA));

        // Both schemas carry their own tables AND their own history table.
        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        foreach (var schema in new[] { schemaA, schemaB })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                  (SELECT count(*) FROM information_schema.tables
                    WHERE table_schema = @s AND table_name = 'conventions'),
                  (SELECT count(*) FROM information_schema.tables
                    WHERE table_schema = @s AND table_name = '__TenantMigrationsHistory')
                """;
            cmd.Parameters.AddWithValue("s", schema);
            await using var r = await cmd.ExecuteReaderAsync();
            (await r.ReadAsync()).Should().BeTrue();
            r.GetInt64(0).Should().Be(1, $"conventions table must exist in schema {schema}");
            r.GetInt64(1).Should().Be(1, $"__TenantMigrationsHistory must exist in schema {schema}");
        }

        // Data isolation: a row written via schema A's context is invisible
        // via schema B's. TenantDbContext has NO query filters, so the only
        // thing that can isolate these reads is the search_path schema.
        var optsA = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(CsFor(schemaA)).Options;
        var optsB = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(CsFor(schemaB)).Options;

        await using (var ctxA = new TenantDbContext(optsA, tenantA))
        {
            // Minimal valid AgentConfig — Id / Config / CreatedAt / UpdatedAt
            // all carry DB-side defaults (gen_random_uuid(), '{}'::jsonb,
            // now()); the tenant context breaks the Tenant navigation so no
            // FK row is required.
            ctxA.AgentConfigs.Add(new AgentConfig { TenantId = tenantA });
            await ctxA.SaveChangesAsync();
        }

        await using (var ctxB = new TenantDbContext(optsB, tenantB))
        {
            (await ctxB.AgentConfigs.AnyAsync()).Should().BeFalse(
                "schema B must not see rows written into schema A");
        }
    }
}
