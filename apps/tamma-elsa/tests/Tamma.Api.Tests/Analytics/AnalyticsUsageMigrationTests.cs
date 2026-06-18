using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Analytics;

/// <summary>
/// Story 36-1 (AC11/AC12) — Postgres 17 Testcontainer proof for the per-tenant
/// analytics fact tables. Follows <c>SchemaPerTenantMigrationTests</c> (two
/// tenant schemas in one DB, search-path isolation) +
/// <c>ConventionStoreMigrationTests</c> (NULLS NOT DISTINCT collision via raw
/// SQL — EF InMemory honours neither, so a real Postgres is the only proof).
///
/// <para>Assertions:</para>
/// <list type="number">
///   <item>The Tenant migration graph (incl. <c>AddAnalyticsUsageFactTables</c>)
///     applies into a <c>t_&lt;hex&gt;</c> search-path schema; both schemas
///     carry their own <c>analytics_usage_hourly</c> + <c>analytics_usage_daily</c>
///     + <c>__TenantMigrationsHistory</c>. Re-running the migrator for an
///     already-migrated schema is a no-op (AC11 + AC12a).</item>
///   <item>A row written through schema A's <see cref="TenantDbContext"/> is
///     invisible through schema B's context — the search-path schema is the
///     only isolation plane (no EF query filter) (AC12b).</item>
///   <item>A second insert of the SAME full dimension tuple within one bucket —
///     with NULL <c>AgentId</c>/<c>WorkflowDefinitionId</c>/<c>RepoId</c> —
///     raises a unique violation, proving NULLS NOT DISTINCT (AC12c).</item>
/// </list>
/// </summary>
[TestFixture]
public class AnalyticsUsageMigrationTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _baseConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("analytics_usage_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _baseConnectionString = _postgres.GetConnectionString();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _postgres.DisposeAsync();
    }

    private string CsFor(string schema) =>
        new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = schema }
            .ConnectionString;

    [Test]
    public async Task Migration_CreatesFactTables_InEachTenantSchema_AndIsolatesAndDedupes()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var schemaA = TenantNaming.SchemaName(tenantA);
        var schemaB = TenantNaming.SchemaName(tenantB);

        var migrator = new EfTenantDbMigrator();
        await migrator.MigrateTenantAppAsync(CsFor(schemaA));
        await migrator.MigrateTenantAppAsync(CsFor(schemaB));
        // AC11/AC12a — idempotency: re-running for A is a no-op, not a failure.
        await migrator.MigrateTenantAppAsync(CsFor(schemaA));

        // ── (a) both schemas carry both fact tables + their own history ──
        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync();
            foreach (var schema in new[] { schemaA, schemaB })
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT
                      (SELECT count(*) FROM information_schema.tables
                        WHERE table_schema = @s AND table_name = 'analytics_usage_hourly'),
                      (SELECT count(*) FROM information_schema.tables
                        WHERE table_schema = @s AND table_name = 'analytics_usage_daily'),
                      (SELECT count(*) FROM information_schema.tables
                        WHERE table_schema = @s AND table_name = '__TenantMigrationsHistory')
                    """;
                cmd.Parameters.AddWithValue("s", schema);
                await using var r = await cmd.ExecuteReaderAsync();
                (await r.ReadAsync()).Should().BeTrue();
                r.GetInt64(0).Should().Be(1, $"analytics_usage_hourly must exist in schema {schema}");
                r.GetInt64(1).Should().Be(1, $"analytics_usage_daily must exist in schema {schema}");
                r.GetInt64(2).Should().Be(1, $"__TenantMigrationsHistory must exist in schema {schema}");
            }
        }

        // ── (b) search-path isolation — a row in schema A is invisible in B ──
        var optsA = new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(CsFor(schemaA)).Options;
        var optsB = new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(CsFor(schemaB)).Options;

        await using (var ctxA = new TenantDbContext(optsA, tenantA))
        {
            ctxA.AnalyticsUsageHourly.Add(new AnalyticsUsageHourly
            {
                Hour = new DateTime(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc),
                Provider = "anthropic-claude",
                CostBasis = CostBasis.Byok,
                // AgentId / WorkflowDefinitionId / RepoId left null on purpose.
            });
            await ctxA.SaveChangesAsync();
        }

        await using (var ctxB = new TenantDbContext(optsB, tenantB))
        {
            (await ctxB.AnalyticsUsageHourly.AnyAsync()).Should().BeFalse(
                "schema B must not see rows written into schema A — search-path is the isolation plane");
        }

        // ── (c) NULLS NOT DISTINCT — duplicate full-NULL-dimension tuple collides ──
        await using (var conn = new NpgsqlConnection(CsFor(schemaA)))
        {
            await conn.OpenAsync();

            // The row above already occupies (Hour, 'anthropic-claude', NULL,
            // NULL, NULL, 'byok'). A second insert of the identical tuple — with
            // every nullable dimension NULL — must collide on UX_*_dims.
            await using var dup = new NpgsqlCommand(
                """
                INSERT INTO analytics_usage_hourly ("Hour", "Provider", "CostBasis")
                VALUES (TIMESTAMPTZ '2026-06-17T12:00:00Z', 'anthropic-claude', 'byok');
                """, conn);

            var act = async () => await dup.ExecuteNonQueryAsync();

            var ex = await act.Should().ThrowAsync<PostgresException>();
            ex.Which.SqlState.Should().Be("23505", // unique_violation
                because: "NULLS NOT DISTINCT — two rows with the same (Hour, Provider, "
                    + "NULL AgentId, NULL WorkflowDefinitionId, NULL RepoId, CostBasis) tuple "
                    + "collide; exactly one row per dimension tuple per bucket");
        }

        // ── (c′) a DIFFERENT non-null dimension must NOT collide ──
        await using (var conn = new NpgsqlConnection(CsFor(schemaA)))
        {
            await conn.OpenAsync();
            await using var distinct = new NpgsqlCommand(
                """
                INSERT INTO analytics_usage_hourly ("Hour", "Provider", "AgentId", "CostBasis")
                VALUES (TIMESTAMPTZ '2026-06-17T12:00:00Z', 'anthropic-claude', 'agent-x', 'byok');
                """, conn);

            var rows = await distinct.ExecuteNonQueryAsync();
            rows.Should().Be(1,
                "a non-null AgentId makes the dimension tuple distinct — it must insert cleanly");
        }
    }

    [Test]
    public async Task UniqueIndex_NullsNotDistinct_Is_Confirmed_In_PgCatalogue()
    {
        var tenant = Guid.NewGuid();
        var schema = TenantNaming.SchemaName(tenant);
        await new EfTenantDbMigrator().MigrateTenantAppAsync(CsFor(schema));

        await using var conn = new NpgsqlConnection(CsFor(schema));
        await conn.OpenAsync();

        foreach (var indexName in new[]
            { "UX_analytics_usage_hourly_dims", "UX_analytics_usage_daily_dims" })
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT ix.indnullsnotdistinct
                FROM pg_class       c
                JOIN pg_index       ix ON ix.indrelid = c.oid
                JOIN pg_class       ic ON ic.oid = ix.indexrelid
                JOIN pg_namespace   n  ON n.oid = c.relnamespace
                WHERE n.nspname = @schema
                  AND ic.relname = @ix;
                """, conn);
            cmd.Parameters.AddWithValue("schema", schema);
            cmd.Parameters.AddWithValue("ix", indexName);

            var result = await cmd.ExecuteScalarAsync();
            result.Should().NotBeNull($"the {indexName} index must exist");
            result.Should().Be(true,
                $"{indexName} must use NULLS NOT DISTINCT so NULL dimensions dedupe");
        }
    }
}
