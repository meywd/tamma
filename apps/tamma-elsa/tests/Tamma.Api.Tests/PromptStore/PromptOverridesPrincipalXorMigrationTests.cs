using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.PromptStore;

/// <summary>
/// Story 27-2 — pin the <c>ck_prompt_overrides_principal_xor</c> CHECK
/// constraint and the <c>NULLS NOT DISTINCT</c> semantics of the dual-key
/// unique index against a real Postgres instance. EF-InMemory doesn't
/// enforce CHECK constraints OR honour NULLS NOT DISTINCT, so the only
/// path to verifying these is a Postgres testcontainer.
///
/// <para>The migration under test is
/// <c>20260429152530_Story27_2_PromptOverridesPrincipalXor</c>. The
/// fixture spins a Postgres 17 container, runs <see cref="EfTenantDbMigrator"/>
/// against an empty database (which executes the full tenant migration
/// graph), then drives the table with raw SQL to assert the constraints
/// fail-closed.</para>
/// </summary>
[TestFixture]
public class PromptOverridesPrincipalXorMigrationTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("prompt_xor_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        // Run the full tenant migration graph — including Story 27-2 — so
        // we test the live shape, not a hand-rolled mirror.
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
        await using var cmd = new NpgsqlCommand("TRUNCATE TABLE prompt_overrides;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task PrincipalXor_AcceptsRow_WithUserIdSetTenantIdNull()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO prompt_overrides
              ("UserId", "TenantId", "Scope", "Role", "Action", "Template", "Variables", "EnableTools", "MaxTokens")
            VALUES (@uid, NULL, 'role-action', 'developer', 'plan', 't', '{}', false, 4096);
            """, conn);
        cmd.Parameters.AddWithValue("uid", Guid.NewGuid());

        var rowsAffected = await cmd.ExecuteNonQueryAsync();

        rowsAffected.Should().Be(1);
    }

    [Test]
    public async Task PrincipalXor_AcceptsRow_WithTenantIdSetUserIdNull()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO prompt_overrides
              ("UserId", "TenantId", "Scope", "Role", "Action", "Template", "Variables", "EnableTools", "MaxTokens")
            VALUES (NULL, @tid, 'role-action', 'developer', 'plan', 't', '{}', false, 4096);
            """, conn);
        cmd.Parameters.AddWithValue("tid", Guid.NewGuid());

        var rowsAffected = await cmd.ExecuteNonQueryAsync();

        rowsAffected.Should().Be(1);
    }

    [Test]
    public async Task PrincipalXor_RejectsRow_WithBothKeysSet()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO prompt_overrides
              ("UserId", "TenantId", "Scope", "Role", "Action", "Template", "Variables", "EnableTools", "MaxTokens")
            VALUES (@uid, @tid, 'role-action', 'developer', 'plan', 't', '{}', false, 4096);
            """, conn);
        cmd.Parameters.AddWithValue("uid", Guid.NewGuid());
        cmd.Parameters.AddWithValue("tid", Guid.NewGuid());

        var act = async () => await cmd.ExecuteNonQueryAsync();

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23514"); // check_violation
        ex.Which.ConstraintName.Should().Be("ck_prompt_overrides_principal_xor");
    }

    [Test]
    public async Task PrincipalXor_RejectsRow_WithBothKeysNull()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO prompt_overrides
              ("UserId", "TenantId", "Scope", "Role", "Action", "Template", "Variables", "EnableTools", "MaxTokens")
            VALUES (NULL, NULL, 'role-action', 'developer', 'plan', 't', '{}', false, 4096);
            """, conn);

        var act = async () => await cmd.ExecuteNonQueryAsync();

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23514"); // check_violation
        ex.Which.ConstraintName.Should().Be("ck_prompt_overrides_principal_xor");
    }

    // ------------------------------------------------------------------
    // NULLS NOT DISTINCT semantics — two SaaS-mode rows with the same
    // (TenantId, Scope, Role, Action) and UserId IS NULL must collide on
    // the unique index, NOT be considered distinct because of the NULLs.
    // ------------------------------------------------------------------

    [Test]
    public async Task UniqueIndex_NullsNotDistinct_RejectsDuplicateTenantRows()
    {
        var tenantId = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using (var insert1 = new NpgsqlCommand(
            """
            INSERT INTO prompt_overrides
              ("UserId", "TenantId", "Scope", "Role", "Action", "Template", "Variables", "EnableTools", "MaxTokens")
            VALUES (NULL, @tid, 'role-action', 'developer', 'plan', 'first', '{}', false, 4096);
            """, conn))
        {
            insert1.Parameters.AddWithValue("tid", tenantId);
            await insert1.ExecuteNonQueryAsync();
        }

        await using var insert2 = new NpgsqlCommand(
            """
            INSERT INTO prompt_overrides
              ("UserId", "TenantId", "Scope", "Role", "Action", "Template", "Variables", "EnableTools", "MaxTokens")
            VALUES (NULL, @tid, 'role-action', 'developer', 'plan', 'second', '{}', false, 4096);
            """, conn);
        insert2.Parameters.AddWithValue("tid", tenantId);

        var act = async () => await insert2.ExecuteNonQueryAsync();

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23505"); // unique_violation
    }

    [Test]
    public async Task UniqueIndex_NullsNotDistinct_RejectsDuplicateUserRows()
    {
        var userId = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using (var insert1 = new NpgsqlCommand(
            """
            INSERT INTO prompt_overrides
              ("UserId", "TenantId", "Scope", "Role", "Action", "Template", "Variables", "EnableTools", "MaxTokens")
            VALUES (@uid, NULL, 'role-action', 'developer', 'plan', 'first', '{}', false, 4096);
            """, conn))
        {
            insert1.Parameters.AddWithValue("uid", userId);
            await insert1.ExecuteNonQueryAsync();
        }

        await using var insert2 = new NpgsqlCommand(
            """
            INSERT INTO prompt_overrides
              ("UserId", "TenantId", "Scope", "Role", "Action", "Template", "Variables", "EnableTools", "MaxTokens")
            VALUES (@uid, NULL, 'role-action', 'developer', 'plan', 'second', '{}', false, 4096);
            """, conn);
        insert2.Parameters.AddWithValue("uid", userId);

        var act = async () => await insert2.ExecuteNonQueryAsync();

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23505"); // unique_violation
    }

    [Test]
    public async Task UniqueIndex_AllowsDifferentTenants_SameRoleAction()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using (var insert1 = new NpgsqlCommand(
            """
            INSERT INTO prompt_overrides
              ("UserId", "TenantId", "Scope", "Role", "Action", "Template", "Variables", "EnableTools", "MaxTokens")
            VALUES (NULL, @tid, 'role-action', 'developer', 'plan', 't1', '{}', false, 4096);
            """, conn))
        {
            insert1.Parameters.AddWithValue("tid", t1);
            await insert1.ExecuteNonQueryAsync();
        }

        await using var insert2 = new NpgsqlCommand(
            """
            INSERT INTO prompt_overrides
              ("UserId", "TenantId", "Scope", "Role", "Action", "Template", "Variables", "EnableTools", "MaxTokens")
            VALUES (NULL, @tid, 'role-action', 'developer', 'plan', 't2', '{}', false, 4096);
            """, conn);
        insert2.Parameters.AddWithValue("tid", t2);

        var rows = await insert2.ExecuteNonQueryAsync();
        rows.Should().Be(1, "two different tenants can both customise the same role/action");
    }
}
