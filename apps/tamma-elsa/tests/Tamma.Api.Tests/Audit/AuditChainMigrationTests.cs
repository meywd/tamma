using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// Story 37-2 (AC1/AC5/AC11) — applies the ControlPlane migration bundle to a
/// clean Postgres 17 testcontainer and asserts the hash-chain schema landed: the
/// <c>chain_sequence</c> unique index, the <c>audit_chain_checkpoints</c> table +
/// its scope↔tenant CHECK, and — the point of AC11 — that the append-only
/// trigger REJECTS a DELETE / a forbidden UPDATE while allowing a one-time
/// NULL→value chain backfill.
/// </summary>
[TestFixture]
public class AuditChainMigrationTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("audit_chain_migration_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        await using var ctx = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);
        await ctx.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        return conn;
    }

    [Test]
    public async Task Migration_Creates_Checkpoint_Table_And_ChainSequence_Index()
    {
        await using var conn = await OpenAsync();

        var tables = await Scalars(conn,
            "SELECT table_name FROM information_schema.tables WHERE table_schema='public';");
        tables.Should().Contain("audit_chain_checkpoints");

        var idx = await Scalars(conn,
            "SELECT indexname FROM pg_indexes WHERE tablename='audit_records';");
        idx.Should().Contain("UX_audit_records_ChainSequence");

        var checks = await Scalars(conn, "SELECT conname FROM pg_constraint WHERE contype='c';");
        checks.Should().Contain("ck_audit_chain_checkpoints_scope_tenant");
    }

    [Test]
    public async Task Checkpoint_ScopeTenant_Check_Rejects_Platform_With_TenantId()
    {
        await using var conn = await OpenAsync();
        var act = async () =>
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO audit_chain_checkpoints
                  ("Scope","TenantId","HeadSequence","HeadHash","SignedAt","Signature","KeyVersion")
                VALUES ('platform', @tid, 1, 'x', now(), '\x01'::bytea, 1);
                """, conn);
            cmd.Parameters.AddWithValue("tid", Guid.NewGuid());
            await cmd.ExecuteNonQueryAsync();
        };
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Test]
    public async Task AppendOnly_Trigger_Rejects_Delete()
    {
        await using var conn = await OpenAsync();
        var id = await InsertChainedRow(conn, seq: 100);

        var act = async () =>
        {
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM audit_records WHERE \"Id\"=@id;", conn);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        };
        (await act.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain("append-only");
    }

    [Test]
    public async Task AppendOnly_Trigger_Rejects_Mutating_A_Core_Field()
    {
        await using var conn = await OpenAsync();
        var id = await InsertChainedRow(conn, seq: 200);

        var act = async () =>
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE audit_records SET \"PayloadJson\"='{\"tampered\":true}'::jsonb WHERE \"Id\"=@id;", conn);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        };
        (await act.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain("append-only");
    }

    [Test]
    public async Task AppendOnly_Trigger_Allows_OneTime_Null_To_Value_Backfill()
    {
        await using var conn = await OpenAsync();
        // Insert a LEGACY row (no chain columns yet).
        var id = Guid.NewGuid();
        var src = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO audit_records
              ("Id","ActionCode","Category","Severity","Outcome","OccurredAt",
               "SourceEventId","SourceSequenceNumber","PayloadJson","TenantId","UserId")
            VALUES (@id,'SECRET.REVEAL','secret','high','success', now(),
                    @src, 1, '{}'::jsonb, NULL, @uid);
            """, conn))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("src", src);
            insert.Parameters.AddWithValue("uid", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync();
        }

        // Backfill the chain columns from NULL → value (allowed exactly once).
        await using var backfill = new NpgsqlCommand(
            """
            UPDATE audit_records
            SET "ChainSequence"=1,
                "PrevRecordHash"='0000000000000000000000000000000000000000000000000000000000000000',
                "RecordHash"='1111111111111111111111111111111111111111111111111111111111111111'
            WHERE "Id"=@id;
            """, conn);
        backfill.Parameters.AddWithValue("id", id);
        var affected = await backfill.ExecuteNonQueryAsync();
        affected.Should().Be(1, "a one-time NULL→value chain backfill is permitted");

        // But a SECOND update of an already-set chain column is rejected.
        var act = async () =>
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE audit_records SET \"RecordHash\"='2222222222222222222222222222222222222222222222222222222222222222' WHERE \"Id\"=@id;", conn);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        };
        (await act.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain("append-only");
    }

    private static async Task<Guid> InsertChainedRow(NpgsqlConnection conn, long seq)
    {
        var id = Guid.NewGuid();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO audit_records
              ("Id","ActionCode","Category","Severity","Outcome","OccurredAt",
               "SourceEventId","SourceSequenceNumber","PayloadJson","TenantId","UserId",
               "ChainSequence","PrevRecordHash","RecordHash")
            VALUES (@id,'SECRET.REVEAL','secret','high','success', now(),
                    @src, @seq, '{}'::jsonb, NULL, @uid,
                    @seq,
                    '0000000000000000000000000000000000000000000000000000000000000000',
                    '1111111111111111111111111111111111111111111111111111111111111111');
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("src", Guid.NewGuid());
        cmd.Parameters.AddWithValue("seq", seq);
        cmd.Parameters.AddWithValue("uid", Guid.NewGuid());
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<HashSet<string>> Scalars(NpgsqlConnection conn, string sql)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (!reader.IsDBNull(0)) result.Add(reader.GetString(0));
        return result;
    }
}
