using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;
using Tamma.Api.Services.Audit;
using Tamma.Core.Audit;
using Tamma.Data;
using Tamma.Data.Audit;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// Story 37-2 (AC1/AC3/AC4) — insert-time chaining + end-to-end verification
/// against an in-memory <see cref="ControlPlaneDbContext"/> (the platform chain).
/// The advisory-lock path is Postgres-only; the in-memory driver exercises the
/// deterministic single-threaded chaining + the pure verifier over real rows.
/// </summary>
[TestFixture]
public class AuditChainRepositoryChainingTests
{
    private static ControlPlaneDbContext NewCp() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static AuditRecord NewRecord() => new()
    {
        Id = Guid.NewGuid(),
        ActionCode = "SECRET.REVEAL",
        Category = "security",
        Severity = "high",
        Outcome = "success",
        OccurredAt = DateTime.UtcNow,
        SourceEventId = Guid.NewGuid(),
        SourceSequenceNumber = Random.Shared.Next(1, 1_000_000),
        PayloadJson = "{}",
        TenantId = null,
        UserId = Guid.NewGuid(), // single-user platform-chain row
    };

    [Test]
    public async Task Sequential_Inserts_Form_A_Contiguous_Genesis_Anchored_Chain()
    {
        await using var cp = NewCp();
        var repo = new AuditRecordRepository();

        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var r = NewRecord();
            (await repo.InsertIfAbsentAsync(cp, r)).Should().BeTrue();
            ids.Add(r.Id);
        }

        var rows = await cp.AuditRecords.AsNoTracking()
            .OrderBy(r => r.ChainSequence).ToListAsync();

        rows.Select(r => r.ChainSequence).Should().Equal(1L, 2L, 3L, 4L);
        rows[0].PrevRecordHash.Should().Be(AuditChainGenesis.HashHex, "the first record chains to genesis");
        for (var i = 1; i < rows.Count; i++)
        {
            rows[i].PrevRecordHash.Should().Be(rows[i - 1].RecordHash,
                "each record's prev_hash links to the prior record's hash");
        }
        rows.Should().OnlyContain(r => r.RecordHash != null && r.RecordHash.Length == 64);
    }

    [Test]
    public async Task Duplicate_SourceEvent_Is_Idempotent_And_Does_Not_Advance_The_Chain()
    {
        await using var cp = NewCp();
        var repo = new AuditRecordRepository();

        var r1 = NewRecord();
        (await repo.InsertIfAbsentAsync(cp, r1)).Should().BeTrue();

        var dup = NewRecord();
        dup.SourceEventId = r1.SourceEventId; // same source event
        (await repo.InsertIfAbsentAsync(cp, dup)).Should().BeFalse();

        (await cp.AuditRecords.CountAsync()).Should().Be(1);
    }

    [Test]
    public async Task Inserted_Chain_Verifies_Ok_End_To_End()
    {
        await using var cp = NewCp();
        var repo = new AuditRecordRepository();
        for (var i = 0; i < 5; i++)
        {
            (await repo.InsertIfAbsentAsync(cp, NewRecord())).Should().BeTrue();
        }

        var result = await BuildVerifier(cp).VerifyAsync(AuditChainScope.Platform, null, null, default);
        result.Status.Should().Be(ChainVerificationStatus.Ok);
        result.RecordsVerified.Should().Be(5);
    }

    [Test]
    public async Task Tampering_A_Persisted_Row_Breaks_Verification_At_Its_Sequence()
    {
        await using var cp = NewCp();
        var repo = new AuditRecordRepository();
        for (var i = 0; i < 5; i++)
        {
            (await repo.InsertIfAbsentAsync(cp, NewRecord())).Should().BeTrue();
        }

        // Simulate a DB-level tamper (the append-only trigger is Postgres-only;
        // in-memory lets us mutate a persisted row to model an attacker with
        // direct write access).
        var victim = await cp.AuditRecords.FirstAsync(r => r.ChainSequence == 3);
        victim.TargetId = "rewritten-by-attacker";
        await cp.SaveChangesAsync();

        var result = await BuildVerifier(cp).VerifyAsync(AuditChainScope.Platform, null, null, default);
        result.Status.Should().Be(ChainVerificationStatus.Tampered);
        result.FirstBrokenLink!.ChainSequence.Should().Be(3);
        result.FirstBrokenLink.Reason.Should().Be(ChainBreakReason.Mutated);
    }

    private static IAuditChainVerifier BuildVerifier(ControlPlaneDbContext cp)
    {
        var source = new AuditChainRecordSource(cp, tenantFactory: null);
        return new AuditChainVerifier(source, new NoCheckpointGateway());
    }

    private sealed class NoCheckpointGateway : IAuditChainCheckpointGateway
    {
        public Task<AuditChainCheckpointView?> GetLastCoveringAsync(
            AuditChainScope scope, long? to, CancellationToken ct) =>
            Task.FromResult<AuditChainCheckpointView?>(null);

        public Task<bool> VerifySignatureAsync(AuditChainCheckpointView checkpoint, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<long?> GetMaxHeadSequenceAsync(AuditChainScope scope, CancellationToken ct) =>
            Task.FromResult<long?>(null);
    }
}
