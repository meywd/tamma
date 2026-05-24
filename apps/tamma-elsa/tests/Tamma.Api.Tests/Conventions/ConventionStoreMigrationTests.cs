using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Conventions;

/// <summary>
/// Story 27-8 — pin the <c>NULLS NOT DISTINCT</c> semantics of the
/// <c>(TenantId, Role, Action)</c> unique index on the <c>conventions</c>
/// table against a real Postgres instance.
///
/// <para>EF InMemory doesn't enforce CHECK constraints OR honour
/// NULLS NOT DISTINCT, so the only path to verifying these constraints is a
/// Postgres testcontainer. The fixture spins Postgres 17, runs
/// <see cref="EfTenantDbMigrator"/> (executes the full tenant migration graph
/// including Story 27-8), then drives the table with raw SQL to assert
/// fail-closed behaviour.</para>
///
/// <para>Key assertions:</para>
/// <list type="number">
///   <item>Two inserts for the same <c>(NULL tenant_id, role, action)</c>
///     — the second must collide (UNIQUE violation). This proves exactly ONE
///     system-default per cell.</item>
///   <item>Two inserts for the same <c>(non-null tenant_id, role, action)</c>
///     — the second must collide. This proves exactly ONE tenant override per
///     cell.</item>
///   <item>Two inserts for the same <c>(role, action)</c> but DIFFERENT
///     tenant_ids — both must succeed. Cross-tenant rows share the same
///     (role, action) key and must coexist.</item>
///   <item>One system-default + one tenant override for the same
///     <c>(role, action)</c> — both must succeed. Two-tier coexistence.</item>
/// </list>
/// </summary>
[TestFixture]
public class ConventionStoreMigrationTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("convention_store_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        // Earlier migrations reference uuid_generate_v4() (mentorship tables)
        // + gen_random_uuid() (most tables) — install both extensions before
        // running the migration graph.
        await using (var ext = new NpgsqlConnection(_connectionString))
        {
            await ext.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";"
              + "CREATE EXTENSION IF NOT EXISTS \"pgcrypto\";",
                ext);
            await cmd.ExecuteNonQueryAsync();
        }

        // Run the full tenant migration graph — including Story 27-8 — so we
        // test the live schema, not a hand-rolled mirror.
        var migrator = new EfTenantDbMigrator();
        await migrator.MigrateTenantAppAsync(_connectionString);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _postgres.DisposeAsync();
    }

    [SetUp]
    public async Task ClearTable()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("TRUNCATE TABLE conventions;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // ──────────────────────────────────────────────────────────────────────
    // Happy-path — rows that must insert successfully.
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Insert_SystemDefaultRow_Succeeds()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO conventions ("TenantId", "Role", "Action", "Body")
            VALUES (NULL, 'developer', 'write-code', 'default body');
            """, conn);

        var rows = await cmd.ExecuteNonQueryAsync();

        rows.Should().Be(1, "a system-default row (NULL tenant_id) must be insertable");
    }

    [Test]
    public async Task Insert_TenantOverrideRow_Succeeds()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO conventions ("TenantId", "Role", "Action", "Body")
            VALUES (@tid, 'developer', 'write-code', 'tenant body');
            """, conn);
        cmd.Parameters.AddWithValue("tid", Guid.NewGuid());

        var rows = await cmd.ExecuteNonQueryAsync();

        rows.Should().Be(1, "a tenant-override row (non-null tenant_id) must be insertable");
    }

    [Test]
    public async Task Insert_SystemDefaultAndTenantOverride_SameRoleAction_BothSucceed()
    {
        var tenantId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using (var insert1 = new NpgsqlCommand(
            """
            INSERT INTO conventions ("TenantId", "Role", "Action", "Body")
            VALUES (NULL, 'developer', 'plan', 'system default');
            """, conn))
        {
            await insert1.ExecuteNonQueryAsync();
        }

        await using var insert2 = new NpgsqlCommand(
            """
            INSERT INTO conventions ("TenantId", "Role", "Action", "Body")
            VALUES (@tid, 'developer', 'plan', 'tenant override');
            """, conn);
        insert2.Parameters.AddWithValue("tid", tenantId);

        var rows = await insert2.ExecuteNonQueryAsync();

        rows.Should().Be(1,
            "a system default and a tenant override for the same (role, action) must coexist — two-tier model");
    }

    [Test]
    public async Task Insert_DifferentTenants_SameRoleAction_BothSucceed()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using (var insert1 = new NpgsqlCommand(
            """
            INSERT INTO conventions ("TenantId", "Role", "Action", "Body")
            VALUES (@tid, 'architect', 'review-code', 'tenant1 body');
            """, conn))
        {
            insert1.Parameters.AddWithValue("tid", t1);
            await insert1.ExecuteNonQueryAsync();
        }

        await using var insert2 = new NpgsqlCommand(
            """
            INSERT INTO conventions ("TenantId", "Role", "Action", "Body")
            VALUES (@tid, 'architect', 'review-code', 'tenant2 body');
            """, conn);
        insert2.Parameters.AddWithValue("tid", t2);

        var rows = await insert2.ExecuteNonQueryAsync();

        rows.Should().Be(1,
            "different tenants may each have their own override for the same (role, action)");
    }

    // ──────────────────────────────────────────────────────────────────────
    // NULLS NOT DISTINCT semantics — duplicate system defaults must collide.
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task UniqueIndex_NullsNotDistinct_RejectsDuplicateSystemDefaultRows()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using (var insert1 = new NpgsqlCommand(
            """
            INSERT INTO conventions ("TenantId", "Role", "Action", "Body")
            VALUES (NULL, 'developer', 'debug', 'first default');
            """, conn))
        {
            await insert1.ExecuteNonQueryAsync();
        }

        await using var insert2 = new NpgsqlCommand(
            """
            INSERT INTO conventions ("TenantId", "Role", "Action", "Body")
            VALUES (NULL, 'developer', 'debug', 'second default — must be rejected');
            """, conn);

        var act = async () => await insert2.ExecuteNonQueryAsync();

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23505", // unique_violation
            because: "NULLS NOT DISTINCT means two rows with NULL tenant_id + same (role, action) " +
                     "collide on the unique index — exactly ONE system default per cell is permitted");
    }

    [Test]
    public async Task UniqueIndex_RejectsDuplicateTenantOverrides()
    {
        var tenantId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using (var insert1 = new NpgsqlCommand(
            """
            INSERT INTO conventions ("TenantId", "Role", "Action", "Body")
            VALUES (@tid, 'developer', 'document', 'first override');
            """, conn))
        {
            insert1.Parameters.AddWithValue("tid", tenantId);
            await insert1.ExecuteNonQueryAsync();
        }

        await using var insert2 = new NpgsqlCommand(
            """
            INSERT INTO conventions ("TenantId", "Role", "Action", "Body")
            VALUES (@tid, 'developer', 'document', 'second override — must be rejected');
            """, conn);
        insert2.Parameters.AddWithValue("tid", tenantId);

        var act = async () => await insert2.ExecuteNonQueryAsync();

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23505", // unique_violation
            because: "a tenant may have exactly ONE override per (role, action) cell");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Schema shape — table exists and carries the expected columns.
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ConventionsTable_HasExpectedColumns()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            SELECT column_name, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_name = 'conventions'
            ORDER BY ordinal_position;
            """, conn);

        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        columns.Should().Contain(new[]
        {
            "Id", "TenantId", "Role", "Action", "Body",
            "Version", "Enabled",
            "CreatedAt", "UpdatedAt",
            "CreatedBy", "UpdatedBy",
        });
    }

    [Test]
    public async Task ConventionsTable_HasNullsNotDistinctUniqueIndex()
    {
        // Confirm the unique index exists and uses NULLS NOT DISTINCT by
        // checking the pg_index catalogue directly.
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            SELECT ix.indnullsnotdistinct
            FROM pg_class       c
            JOIN pg_index       ix  ON ix.indrelid = c.oid
            JOIN pg_class       ic  ON ic.oid      = ix.indexrelid
            WHERE c.relname  = 'conventions'
              AND ic.relname = 'IX_conventions_TenantId_Role_Action';
            """, conn);

        var result = await cmd.ExecuteScalarAsync();

        result.Should().NotBeNull("the IX_conventions_TenantId_Role_Action index must exist");
        result.Should().Be(true,
            because: "the index must use NULLS NOT DISTINCT to enforce exactly-one system-default per cell");
    }
}
