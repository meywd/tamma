using FluentAssertions;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.AcceptanceRules;

/// <summary>
/// Story 39-5 AC6 (schema) — pin the <c>ck_acceptance_rules_overrides_principal_xor</c>
/// CHECK and the <c>NULLS NOT DISTINCT</c> semantics of the dual-key unique index
/// against a real Postgres instance, plus a jsonb round-trip through the real
/// <c>RulesJson</c> column. EF-InMemory enforces neither CHECK constraints nor
/// NULLS NOT DISTINCT, so a Postgres testcontainer is the only path.
///
/// <para>REQUIRES DOCKER to run (CI-verified). The migration under test is
/// <c>AddAcceptanceRulesOverrides</c>; the fixture spins Postgres 17, runs the full
/// tenant migration graph via <see cref="EfTenantDbMigrator"/>, then drives the
/// table with raw SQL.</para>
/// </summary>
[TestFixture]
[Category("Docker")]
public class AcceptanceRulesOverridesMigrationTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    private const string SampleRules =
        "{\"autonomyLevel\":70,\"maxRevisionRounds\":2,\"maxValidationRepairAttempts\":2," +
        "\"ambiguityEscalationThreshold\":0.7,\"alwaysEscalate\":[]," +
        "\"reviewerSelection\":{\"mode\":\"single-reviewer\",\"reviewerRole\":\"architect\"," +
        "\"panelRoles\":[],\"quorum\":null,\"decisionRule\":\"unanimous\"}," +
        "\"decisionGuidance\":\"d\",\"routingGuidance\":\"r\"}";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("acceptance_rules_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        var migrator = new EfTenantDbMigrator();
        await migrator.MigrateTenantAppAsync(_connectionString);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _postgres.DisposeAsync();

    [SetUp]
    public async Task ClearTable()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("TRUNCATE TABLE acceptance_rules_overrides;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task PrincipalXor_accepts_user_row()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO acceptance_rules_overrides ("UserId", "TenantId", "DocumentTypeKey", "RulesJson")
            VALUES (@uid, NULL, 'plan', @rules::jsonb);
            """, conn);
        cmd.Parameters.AddWithValue("uid", Guid.NewGuid());
        cmd.Parameters.AddWithValue("rules", SampleRules);
        (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
    }

    [Test]
    public async Task PrincipalXor_accepts_tenant_base_row_with_null_type_key()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO acceptance_rules_overrides ("UserId", "TenantId", "DocumentTypeKey", "RulesJson")
            VALUES (NULL, @tid, NULL, @rules::jsonb);
            """, conn);
        cmd.Parameters.AddWithValue("tid", Guid.NewGuid());
        cmd.Parameters.AddWithValue("rules", SampleRules);
        (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
    }

    [Test]
    public async Task PrincipalXor_rejects_both_keys_set()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO acceptance_rules_overrides ("UserId", "TenantId", "DocumentTypeKey", "RulesJson")
            VALUES (@uid, @tid, 'plan', @rules::jsonb);
            """, conn);
        cmd.Parameters.AddWithValue("uid", Guid.NewGuid());
        cmd.Parameters.AddWithValue("tid", Guid.NewGuid());
        cmd.Parameters.AddWithValue("rules", SampleRules);

        var ex = await FluentActions.Awaiting(() => cmd.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23514");
        ex.Which.ConstraintName.Should().Be("ck_acceptance_rules_overrides_principal_xor");
    }

    [Test]
    public async Task PrincipalXor_rejects_both_keys_null()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO acceptance_rules_overrides ("UserId", "TenantId", "DocumentTypeKey", "RulesJson")
            VALUES (NULL, NULL, 'plan', @rules::jsonb);
            """, conn);
        cmd.Parameters.AddWithValue("rules", SampleRules);

        var ex = await FluentActions.Awaiting(() => cmd.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23514");
        ex.Which.ConstraintName.Should().Be("ck_acceptance_rules_overrides_principal_xor");
    }

    [Test]
    public async Task UniqueIndex_NullsNotDistinct_rejects_duplicate_base_rows()
    {
        var tenantId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using (var insert1 = new NpgsqlCommand(
            """
            INSERT INTO acceptance_rules_overrides ("UserId", "TenantId", "DocumentTypeKey", "RulesJson")
            VALUES (NULL, @tid, NULL, @rules::jsonb);
            """, conn))
        {
            insert1.Parameters.AddWithValue("tid", tenantId);
            insert1.Parameters.AddWithValue("rules", SampleRules);
            await insert1.ExecuteNonQueryAsync();
        }

        await using var insert2 = new NpgsqlCommand(
            """
            INSERT INTO acceptance_rules_overrides ("UserId", "TenantId", "DocumentTypeKey", "RulesJson")
            VALUES (NULL, @tid, NULL, @rules::jsonb);
            """, conn);
        insert2.Parameters.AddWithValue("tid", tenantId);
        insert2.Parameters.AddWithValue("rules", SampleRules);

        // Two (NULL user, tenant, NULL key) base rows must collide despite the NULLs.
        var ex = await FluentActions.Awaiting(() => insert2.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23505");
    }

    [Test]
    public async Task RulesJson_roundtrips_through_the_real_jsonb_column()
    {
        var tenantId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO acceptance_rules_overrides ("UserId", "TenantId", "DocumentTypeKey", "RulesJson")
            VALUES (NULL, @tid, 'design', @rules::jsonb);
            """, conn))
        {
            insert.Parameters.AddWithValue("tid", tenantId);
            insert.Parameters.AddWithValue("rules", SampleRules);
            await insert.ExecuteNonQueryAsync();
        }

        await using var read = new NpgsqlCommand(
            "SELECT \"RulesJson\"->>'autonomyLevel' FROM acceptance_rules_overrides WHERE \"TenantId\" = @tid;", conn);
        read.Parameters.AddWithValue("tid", tenantId);
        var value = (string?)await read.ExecuteScalarAsync();
        value.Should().Be("70");
    }
}
