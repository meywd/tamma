using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.Audit;
using Tamma.Core.Audit;
using Tamma.Data;
using Tamma.Data.Audit;
using Tamma.Data.Entities;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// Story 37-2 (code-review fixes) — end-to-end hash-chain integrity against a REAL
/// Postgres 17, which the InMemory tests cannot exercise (InMemory stores
/// <c>PayloadJson</c> verbatim and has no triggers).
///
/// <para><b>Finding 1 (CRITICAL).</b> A record inserted through
/// <see cref="AuditRecordRepository"/> — whose chain hash is computed over the
/// in-memory payload string — must verify as <c>Ok</c> after being READ BACK from
/// the column. When <c>PayloadJson</c> was <c>jsonb</c>, Postgres reordered keys /
/// stripped whitespace / normalized numbers on round-trip, so the recomputed hash
/// never matched and EVERY chain verified as <c>Tampered</c> at sequence 1. The
/// fix stores it as <c>text</c> (verbatim bytes).</para>
///
/// <para><b>Finding 2 (IMPORTANT).</b> The signed checkpoint anchor must itself be
/// append-only, and a tail-truncation below a surviving checkpoint must verify as
/// <c>Tampered</c>.</para>
///
/// <para>Uses a fresh container per test so the append-only chain state stays
/// isolated (records cannot be deleted between tests).</para>
/// </summary>
[TestFixture]
public class AuditChainPostgresVerifyTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [SetUp]
    public async Task SetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("audit_chain_verify_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        await using var ctx = NewCp();
        await ctx.Database.MigrateAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    private ControlPlaneDbContext NewCp() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private static AuditRecord NewRecord(string payloadJson) => new()
    {
        Id = Guid.NewGuid(),
        ActionCode = "SECRET.REVEAL",
        Category = "security",
        Severity = "high",
        Outcome = "success",
        OccurredAt = DateTime.UtcNow,
        SourceEventId = Guid.NewGuid(),
        SourceSequenceNumber = Random.Shared.Next(1, 1_000_000),
        PayloadJson = payloadJson,
        TenantId = null,
        UserId = Guid.NewGuid(),
    };

    // ── Finding 1 (CRITICAL) — jsonb round-trip broke canonicalization ───────────

    [Test]
    public async Task Record_With_Unsorted_MultiKey_Payload_Verifies_Ok_On_Real_Postgres()
    {
        await using var cp = NewCp();
        var repo = new AuditRecordRepository();

        // Keys NOT already sorted, nested objects, a float and a string — the exact
        // shape jsonb would rewrite on storage. With `text` the bytes are verbatim.
        var first = NewRecord("{\"tags\":{\"z\":1,\"a\":2},\"data\":{\"n\":1.0,\"m\":\"x\"}}");
        (await repo.InsertIfAbsentAsync(cp, first)).Should().BeTrue();
        (await repo.InsertIfAbsentAsync(cp, NewRecord("{\"b\":2,\"a\":1}"))).Should().BeTrue();
        (await repo.InsertIfAbsentAsync(cp, NewRecord("{}"))).Should().BeTrue();

        // Sanity: the payload was stored verbatim (no jsonb re-serialization).
        await using (var readCp = NewCp())
        {
            var stored = await readCp.AuditRecords.AsNoTracking()
                .Where(r => r.Id == first.Id).Select(r => r.PayloadJson).FirstAsync();
            stored.Should().Be("{\"tags\":{\"z\":1,\"a\":2},\"data\":{\"n\":1.0,\"m\":\"x\"}}");
        }

        // The whole chain, read back from Postgres, recomputes byte-identically.
        var result = await BuildVerifier(cp).VerifyAsync(AuditChainScope.Platform, null, null, default);

        result.Status.Should().Be(ChainVerificationStatus.Ok,
            "a record inserted through the repository must verify Ok against real Postgres "
            + "(jsonb round-trip would have made it Tampered at sequence 1)");
        result.RecordsVerified.Should().Be(3);
    }

    // ── Finding 2a — the checkpoint table is append-only ─────────────────────────

    [Test]
    public async Task Checkpoint_AppendOnly_Trigger_Rejects_Delete_And_Update()
    {
        var id = await InsertCheckpoint(headSequence: 5, headHash: new string('a', 64));

        await using var conn = await OpenAsync();

        var delete = async () =>
        {
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM audit_chain_checkpoints WHERE \"Id\"=@id;", conn);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        };
        (await delete.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain("append-only");

        var update = async () =>
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE audit_chain_checkpoints SET \"HeadSequence\"=1 WHERE \"Id\"=@id;", conn);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        };
        (await update.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain("append-only");
    }

    // ── Finding 2b — tail-truncation below a surviving checkpoint is Tampered ─────

    [Test]
    public async Task Tail_Truncation_Below_A_Surviving_Checkpoint_Verifies_Tampered()
    {
        await using var cp = NewCp();
        var repo = new AuditRecordRepository();

        var records = new List<AuditRecord>();
        for (var i = 0; i < 5; i++)
        {
            var r = NewRecord("{\"i\":" + i + "}");
            (await repo.InsertIfAbsentAsync(cp, r)).Should().BeTrue();
            records.Add(r);
        }
        var head = records.Single(r => r.ChainSequence == 5);

        // A signed checkpoint anchors head=5 (written before the attack). It is
        // append-only, so the attacker cannot delete it.
        await InsertCheckpoint(headSequence: 5, headHash: head.RecordHash!);

        // Attacker with direct DB access bypasses the ORM append-only trigger and
        // deletes the two most recent records → the live head regresses to 3.
        await TruncateRecordsAbove(sequence: 3);

        var verifier = BuildVerifier(cp);

        // Full verify: the covering (max) checkpoint at 5 is confirmed to anchor a
        // now-missing head record → Tampered.
        var full = await verifier.VerifyAsync(AuditChainScope.Platform, null, null, default);
        full.Status.Should().Be(ChainVerificationStatus.Tampered,
            "the surviving checkpoint reveals that records were deleted from the tail");

        // Bounded verify to the truncated head (3): the covering-checkpoint filter
        // finds nothing <= 3, so the explicit head < max-checkpoint check must fire.
        var bounded = await verifier.VerifyAsync(AuditChainScope.Platform, null, 3, default);
        bounded.Status.Should().Be(ChainVerificationStatus.Tampered);
        bounded.FirstBrokenLink!.Reason.Should().Be(ChainBreakReason.HeadBelowCheckpoint);
        bounded.FirstBrokenLink.ChainSequence.Should().Be(5);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private IAuditChainVerifier BuildVerifier(ControlPlaneDbContext cp) =>
        new AuditChainVerifier(
            new AuditChainRecordSource(cp, tenantFactory: null),
            new AuditChainCheckpointGateway(cp, new AlwaysValidSigner()));

    private async Task<Guid> InsertCheckpoint(long headSequence, string headHash)
    {
        var id = Guid.NewGuid();
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO audit_chain_checkpoints
              ("Id","Scope","TenantId","HeadSequence","HeadHash","SignedAt","Signature","KeyVersion")
            VALUES (@id,'platform',NULL,@seq,@hash, now(), '\x01'::bytea, 1);
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("seq", headSequence);
        cmd.Parameters.AddWithValue("hash", headHash);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>
    /// Simulate an attacker with direct DB write access clipping the tail: the ORM
    /// append-only trigger is a defence against accidental writes, not a security
    /// boundary (a table owner can DISABLE TRIGGER), so we do exactly that to model
    /// the threat the hash-chain + checkpoint anchor must still detect.
    /// </summary>
    private async Task TruncateRecordsAbove(long sequence)
    {
        await using var conn = await OpenAsync();
        await using var disable = new NpgsqlCommand(
            "ALTER TABLE audit_records DISABLE TRIGGER trg_audit_records_append_only;", conn);
        await disable.ExecuteNonQueryAsync();

        await using (var del = new NpgsqlCommand(
            "DELETE FROM audit_records WHERE \"ChainSequence\" > @seq;", conn))
        {
            del.Parameters.AddWithValue("seq", sequence);
            await del.ExecuteNonQueryAsync();
        }

        await using var enable = new NpgsqlCommand(
            "ALTER TABLE audit_records ENABLE TRIGGER trg_audit_records_append_only;", conn);
        await enable.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        return conn;
    }

    private sealed class AlwaysValidSigner : IAuditChainSigner
    {
        public Task<(byte[] Signature, int KeyVersion)> SignAsync(
            string scope, Guid? tenantId, long headSequence, string headHashHex,
            DateTime signedAt, CancellationToken ct = default) =>
            Task.FromResult((new byte[] { 1 }, 1));

        public Task<bool> VerifyAsync(AuditChainCheckpointView checkpoint, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}
