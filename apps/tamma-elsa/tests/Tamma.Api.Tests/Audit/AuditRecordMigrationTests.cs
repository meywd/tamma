using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// Story 37-1 (AC5, AC6, AC8) — applies the ControlPlane migration bundle to a
/// clean Postgres 17 testcontainer and asserts the audit schema landed: both
/// new tables, the principal-XOR + outcome CHECK constraints, the unique
/// <c>source_event_id</c> idempotency index, and that the XOR + unique
/// constraints actually reject the bad rows. Mirrors <c>BillingMigrationTests</c>.
/// </summary>
[TestFixture]
public class AuditRecordMigrationTests
{
    private const string ThisMigration = "20260619003618_AddAuditRecords";
    private const string PrevMigration = "20260618212532_AddBillingCustomerAndPlanPrices";

    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("audit_migration_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        await using var ctx = NewContext();
        await ctx.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<HashSet<string>> QueryStringsAsync(NpgsqlConnection conn, string sql)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (!reader.IsDBNull(0)) result.Add(reader.GetString(0));
        return result;
    }

    [Test]
    public async Task Migration_Creates_Both_Audit_Tables()
    {
        await using var conn = await OpenAsync();
        var tables = await QueryStringsAsync(conn,
            "SELECT table_name FROM information_schema.tables WHERE table_schema='public';");

        tables.Should().Contain("audit_records");
        tables.Should().Contain("audit_projector_cursor");
    }

    // ── C1 — the cursor table is keyed per-(ProjectorId, TenantId) ──

    [Test]
    public async Task Cursor_Table_Has_TenantId_Column_In_Composite_PrimaryKey()
    {
        await using var conn = await OpenAsync();

        // The TenantId column exists (uuid, not null — it is a key column).
        var cols = await QueryStringsAsync(conn,
            "SELECT column_name FROM information_schema.columns "
            + "WHERE table_schema='public' AND table_name='audit_projector_cursor';");
        cols.Should().Contain("TenantId",
            "C1 — the domain cursor is tracked per tenant");

        // The primary key is the composite (ProjectorId, TenantId).
        var pkCols = await QueryStringsAsync(conn,
            """
            SELECT a.attname
            FROM   pg_index i
            JOIN   pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
            WHERE  i.indrelid = 'public.audit_projector_cursor'::regclass AND i.indisprimary;
            """);
        pkCols.Should().BeEquivalentTo(new[] { "ProjectorId", "TenantId" },
            "the cursor primary key must be the composite (ProjectorId, TenantId)");
    }

    [Test]
    public async Task Cursor_Table_Allows_One_Row_Per_Tenant_Same_Projector()
    {
        await using var conn = await OpenAsync();
        var ta = Guid.NewGuid();
        var tb = Guid.NewGuid();

        // Two tenants under the same projector id — both rows must be accepted
        // (independent per-tenant domain high-water marks).
        (await InsertCursorRow(conn, "default", ta, 5)).Should().Be(1);
        (await InsertCursorRow(conn, "default", tb, 1)).Should().Be(1);

        // The same (projector, tenant) twice violates the composite PK.
        var act = async () => await InsertCursorRow(conn, "default", ta, 9);
        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23505"); // unique_violation
    }

    private static async Task<int> InsertCursorRow(
        NpgsqlConnection conn, string projectorId, Guid tenantId, long lastDomainSeq)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO audit_projector_cursor
              ("ProjectorId","TenantId","LastDomainSequenceNumber","LastPlatformSequenceNumber","UpdatedAt")
            VALUES (@pid, @tid, @seq, 0, now());
            """, conn);
        cmd.Parameters.AddWithValue("pid", projectorId);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("seq", lastDomainSeq);
        return await cmd.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task Migration_Creates_The_XOR_And_Outcome_Check_Constraints()
    {
        await using var conn = await OpenAsync();
        var checks = await QueryStringsAsync(conn,
            "SELECT conname FROM pg_constraint WHERE contype='c';");

        checks.Should().Contain("ck_audit_records_principal_xor");
        checks.Should().Contain("ck_audit_records_outcome");
    }

    [Test]
    public async Task Migration_Creates_The_Unique_SourceEventId_Index()
    {
        await using var conn = await OpenAsync();
        var idx = await QueryStringsAsync(conn,
            "SELECT indexname FROM pg_indexes WHERE tablename='audit_records';");

        idx.Should().Contain("UX_audit_records_SourceEventId");
        idx.Should().Contain("IX_audit_records_SourceSequenceNumber");
    }

    // ── AC5 — the XOR CHECK rejects both-set and neither-set ──

    [Test]
    public async Task XorCheck_Rejects_BothOwners_Set()
    {
        await using var conn = await OpenAsync();
        var act = async () => await InsertRow(conn,
            tenantId: Guid.NewGuid(), userId: Guid.NewGuid(), sourceId: Guid.NewGuid());
        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23514"); // check_violation
    }

    [Test]
    public async Task XorCheck_Accepts_Neither_Owner_Set_For_PlatformRow()
    {
        // AC11 — a SaaS platform-scope row (impersonation against the platform)
        // legitimately has neither owner; it lives in the CP store. The CHECK is
        // "not both", so this is accepted (it is NOT a strict exactly-one XOR).
        await using var conn = await OpenAsync();
        (await InsertRow(conn, tenantId: null, userId: null, sourceId: Guid.NewGuid()))
            .Should().Be(1);
    }

    [Test]
    public async Task XorCheck_Accepts_TenantOnly_And_UserOnly()
    {
        await using var conn = await OpenAsync();
        (await InsertRow(conn, tenantId: Guid.NewGuid(), userId: null, sourceId: Guid.NewGuid()))
            .Should().Be(1);
        (await InsertRow(conn, tenantId: null, userId: Guid.NewGuid(), sourceId: Guid.NewGuid()))
            .Should().Be(1);
    }

    // ── AC8 — the unique source_event_id index rejects a duplicate ──

    [Test]
    public async Task Unique_SourceEventId_Rejects_Duplicate()
    {
        await using var conn = await OpenAsync();
        var sourceId = Guid.NewGuid();
        (await InsertRow(conn, tenantId: Guid.NewGuid(), userId: null, sourceId: sourceId))
            .Should().Be(1);

        var act = async () =>
            await InsertRow(conn, tenantId: Guid.NewGuid(), userId: null, sourceId: sourceId);
        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23505"); // unique_violation
    }

    // ── AC6 — migration rolls back + forward cleanly ──

    [Test]
    public async Task Migration_Down_Drops_Both_Tables_Then_Up_Restores()
    {
        await using (var down = NewContext())
            await down.GetService<IMigrator>().MigrateAsync(PrevMigration);

        await using (var conn = await OpenAsync())
        {
            var tables = await QueryStringsAsync(conn,
                "SELECT table_name FROM information_schema.tables WHERE table_schema='public';");
            tables.Should().NotContain("audit_records");
            tables.Should().NotContain("audit_projector_cursor");
        }

        await using (var up = NewContext())
            await up.GetService<IMigrator>().MigrateAsync(ThisMigration);

        await using (var conn = await OpenAsync())
        {
            var tables = await QueryStringsAsync(conn,
                "SELECT table_name FROM information_schema.tables WHERE table_schema='public';");
            tables.Should().Contain("audit_records");
            tables.Should().Contain("audit_projector_cursor");
        }
    }

    private static async Task<int> InsertRow(
        NpgsqlConnection conn, Guid? tenantId, Guid? userId, Guid sourceId)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO audit_records
              ("ActionCode","Category","Severity","Outcome","OccurredAt",
               "SourceEventId","SourceSequenceNumber","PayloadJson","TenantId","UserId")
            VALUES
              ('SECRET.REVEAL','secret','critical','success', now(),
               @src, 1, '{}'::jsonb, @tid, @uid);
            """, conn);
        cmd.Parameters.AddWithValue("src", sourceId);
        cmd.Parameters.AddWithValue("tid", (object?)tenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("uid", (object?)userId ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }
}
