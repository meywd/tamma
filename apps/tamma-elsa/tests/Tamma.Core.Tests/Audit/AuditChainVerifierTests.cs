using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Audit;

namespace Tamma.Core.Tests.Audit;

/// <summary>
/// Story 37-2 (AC4/AC7/AC14) — the tamper-detection matrix, run against an
/// in-memory chain built with the REAL canonicalizer + hasher so the verifier
/// recomputes byte-identical hashes.
/// </summary>
[TestFixture]
public class AuditChainVerifierTests
{
    private static readonly AuditChainScope Scope = AuditChainScope.Platform;

    // ── helpers ──────────────────────────────────────────────────────────────

    private static AuditChainRecordView Content(long seq) => new()
    {
        Id = Guid.NewGuid(),
        Discriminator = Scope.Discriminator,
        TenantId = null,
        UserId = null,
        ActionCode = "SECRET.REVEAL",
        Category = "security",
        Severity = "high",
        ActorUserId = null,
        ActorEmailSnapshot = $"user{seq}@example.com",
        TargetType = "secret",
        TargetId = $"target-{seq}",
        Outcome = "success",
        IpAddress = null,
        UserAgent = null,
        OccurredAt = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seq),
        SourceEventId = Guid.NewGuid(),
        SourceSequenceNumber = seq,
        PayloadJson = $"{{\"seq\":{seq}}}",
        ChainSequence = seq,
        PrevRecordHash = string.Empty,
        RecordHash = string.Empty,
    };

    /// <summary>Build a valid chain 1..n with correct prev/record hashes.</summary>
    private static List<AuditChainRecordView> BuildChain(int n)
    {
        var chain = new List<AuditChainRecordView>();
        var prev = AuditChainGenesis.HashHex;
        for (long seq = 1; seq <= n; seq++)
        {
            var withPrev = Content(seq) with { PrevRecordHash = prev };
            var hash = AuditChainHasher.ComposeHex(
                prev, AuditRecordCanonicalizer.ToBytes(withPrev));
            var final = withPrev with { RecordHash = hash };
            chain.Add(final);
            prev = hash;
        }
        return chain;
    }

    private static AuditChainVerifier VerifierFor(
        List<AuditChainRecordView> chain,
        AuditChainCheckpointView? cp = null,
        bool signatureValid = true) =>
        new(new FakeRecordSource(chain), new FakeCheckpointGateway(cp, signatureValid));

    // ── tests ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Clean_Chain_Verifies_Ok()
    {
        var chain = BuildChain(5);
        var result = await VerifierFor(chain).VerifyAsync(Scope, null, null, default);
        result.Status.Should().Be(ChainVerificationStatus.Ok);
        result.RecordsVerified.Should().Be(5);
        result.LastSequence.Should().Be(5);
    }

    [Test]
    public async Task Empty_Chain_Verifies_Ok()
    {
        var result = await VerifierFor(new List<AuditChainRecordView>()).VerifyAsync(Scope, null, null, default);
        result.Status.Should().Be(ChainVerificationStatus.Ok);
        result.RecordsVerified.Should().Be(0);
    }

    [Test]
    public async Task Mutated_Record_Is_Detected_At_Its_Sequence()
    {
        var chain = BuildChain(5);
        // In-place edit of record #3's payload — hash no longer matches.
        chain[2] = chain[2] with { PayloadJson = "{\"seq\":3,\"tampered\":true}" };

        var result = await VerifierFor(chain).VerifyAsync(Scope, null, null, default);
        result.Status.Should().Be(ChainVerificationStatus.Tampered);
        result.FirstBrokenLink!.Reason.Should().Be(ChainBreakReason.Mutated);
        result.FirstBrokenLink.ChainSequence.Should().Be(3);
    }

    [Test]
    public async Task Deleted_Middle_Record_Is_Detected_As_Missing_Gap()
    {
        var chain = BuildChain(5);
        chain.RemoveAt(2); // drop sequence 3 → 1,2,4,5

        var result = await VerifierFor(chain).VerifyAsync(Scope, null, null, default);
        result.Status.Should().Be(ChainVerificationStatus.Tampered);
        result.FirstBrokenLink!.Reason.Should().Be(ChainBreakReason.Missing);
        result.FirstBrokenLink.ChainSequence.Should().Be(3, "sequence 3 is the missing slot");
    }

    [Test]
    public async Task Reordered_Records_Are_Detected()
    {
        var chain = BuildChain(5);
        // Swap the sequence numbers of #2 and #3 without fixing hashes → the
        // stream now yields 1,3,2,... (source orders by ChainSequence).
        var a = chain[1] with { ChainSequence = 3 };
        var b = chain[2] with { ChainSequence = 2 };
        chain[1] = b; // seq 2 slot now holds record with old content #3
        chain[2] = a;

        var result = await VerifierFor(chain).VerifyAsync(Scope, null, null, default);
        result.Status.Should().Be(ChainVerificationStatus.Tampered);
        result.FirstBrokenLink!.Reason.Should()
            .BeOneOf(ChainBreakReason.PrevHashMismatch, ChainBreakReason.Mutated);
    }

    [Test]
    public async Task Clipped_Tail_Against_Checkpoint_Is_Detected()
    {
        var chain = BuildChain(5);
        var head = chain[^1];
        var cp = new AuditChainCheckpointView
        {
            Id = Guid.NewGuid(),
            Scope = Scope.Discriminator,
            TenantId = null,
            HeadSequence = 5,
            HeadHash = head.RecordHash,
            SignedAt = DateTime.UtcNow,
            Signature = new byte[] { 1 },
            KeyVersion = 1,
        };
        // Attacker clips the tail: records 4 and 5 removed, but the checkpoint
        // still anchors head=5.
        chain.RemoveRange(3, 2); // now 1,2,3

        var result = await VerifierFor(chain, cp).VerifyAsync(Scope, null, null, default);
        result.Status.Should().Be(ChainVerificationStatus.Tampered);
        result.FirstBrokenLink!.Reason.Should().Be(ChainBreakReason.Missing);
    }

    [Test]
    public async Task Valid_Checkpoint_Confirms_Ok()
    {
        var chain = BuildChain(5);
        var head = chain[^1];
        var cp = new AuditChainCheckpointView
        {
            Id = Guid.NewGuid(),
            Scope = Scope.Discriminator,
            TenantId = null,
            HeadSequence = 5,
            HeadHash = head.RecordHash,
            SignedAt = DateTime.UtcNow,
            Signature = new byte[] { 1 },
            KeyVersion = 1,
        };
        var result = await VerifierFor(chain, cp).VerifyAsync(Scope, null, null, default);
        result.Status.Should().Be(ChainVerificationStatus.Ok);
        result.LastCheckpoint.Should().NotBeNull();
    }

    [Test]
    public async Task Invalid_Checkpoint_Signature_Is_Reported_Distinctly()
    {
        var chain = BuildChain(3);
        var head = chain[^1];
        var cp = new AuditChainCheckpointView
        {
            Id = Guid.NewGuid(),
            Scope = Scope.Discriminator,
            TenantId = null,
            HeadSequence = 3,
            HeadHash = head.RecordHash,
            SignedAt = DateTime.UtcNow,
            Signature = new byte[] { 9 },
            KeyVersion = 1,
        };
        var result = await VerifierFor(chain, cp, signatureValid: false)
            .VerifyAsync(Scope, null, null, default);
        result.Status.Should().Be(ChainVerificationStatus.Tampered);
        result.FirstBrokenLink!.Reason.Should().Be(ChainBreakReason.CheckpointSignatureInvalid);
    }

    [Test]
    public async Task Checkpoint_Head_Hash_Mismatch_Is_Detected()
    {
        var chain = BuildChain(3);
        var cp = new AuditChainCheckpointView
        {
            Id = Guid.NewGuid(),
            Scope = Scope.Discriminator,
            TenantId = null,
            HeadSequence = 3,
            HeadHash = new string('a', 64), // wrong head hash, valid signature
            SignedAt = DateTime.UtcNow,
            Signature = new byte[] { 1 },
            KeyVersion = 1,
        };
        var result = await VerifierFor(chain, cp).VerifyAsync(Scope, null, null, default);
        result.Status.Should().Be(ChainVerificationStatus.Tampered);
        result.FirstBrokenLink!.Reason.Should().Be(ChainBreakReason.CheckpointHeadMismatch);
    }

    [Test]
    public async Task Tail_Truncated_Below_A_Surviving_Checkpoint_Is_Detected()
    {
        // Records 4 and 5 were deleted (attacker with DB write access), leaving a
        // clean, self-consistent 1..3. But a signed, append-only checkpoint still
        // anchors head=5 — proof those records once existed.
        var chain = BuildChain(3);
        var cp = new AuditChainCheckpointView
        {
            Id = Guid.NewGuid(),
            Scope = Scope.Discriminator,
            TenantId = null,
            HeadSequence = 5,
            HeadHash = new string('a', 64),
            SignedAt = DateTime.UtcNow,
            Signature = new byte[] { 1 },
            KeyVersion = 1,
        };

        // Verify with to=3 so the covering-checkpoint block (which filters
        // head_sequence <= to) finds nothing — the surviving max checkpoint at 5
        // is what must reveal the truncation.
        var result = await VerifierFor(chain, cp).VerifyAsync(Scope, from: null, to: 3, default);

        result.Status.Should().Be(ChainVerificationStatus.Tampered);
        result.FirstBrokenLink!.Reason.Should().Be(ChainBreakReason.HeadBelowCheckpoint);
        result.FirstBrokenLink.ChainSequence.Should().Be(5, "the checkpoint proves head reached 5");
    }

    [Test]
    public async Task Mid_Chain_Range_Anchors_Prev_From_Prior_Record()
    {
        var chain = BuildChain(5);
        // Verify only 3..5 — the verifier anchors prev from record 2's hash.
        var result = await VerifierFor(chain).VerifyAsync(Scope, from: 3, to: 5, default);
        result.Status.Should().Be(ChainVerificationStatus.Ok);
        result.RecordsVerified.Should().Be(3);
    }

    // ── fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeRecordSource : IAuditChainRecordSource
    {
        private readonly List<AuditChainRecordView> _records;
        public FakeRecordSource(List<AuditChainRecordView> records) => _records = records;

        public async IAsyncEnumerable<AuditChainRecordView> StreamAsync(
            AuditChainScope scope, long? from, long? to,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var r in _records
                .Where(r => (from is null || r.ChainSequence >= from) && (to is null || r.ChainSequence <= to))
                .OrderBy(r => r.ChainSequence))
            {
                yield return r;
            }
            await Task.CompletedTask;
        }

        public Task<string?> GetRecordHashAtAsync(AuditChainScope scope, long sequence, CancellationToken ct) =>
            Task.FromResult(_records.FirstOrDefault(r => r.ChainSequence == sequence)?.RecordHash);

        public Task<AuditChainHead?> GetHeadAsync(AuditChainScope scope, CancellationToken ct)
        {
            var head = _records.OrderByDescending(r => r.ChainSequence).FirstOrDefault();
            return Task.FromResult(head is null ? null : new AuditChainHead(head.ChainSequence, head.RecordHash));
        }
    }

    private sealed class FakeCheckpointGateway : IAuditChainCheckpointGateway
    {
        private readonly AuditChainCheckpointView? _cp;
        private readonly bool _signatureValid;
        public FakeCheckpointGateway(AuditChainCheckpointView? cp, bool signatureValid)
        {
            _cp = cp;
            _signatureValid = signatureValid;
        }

        public Task<AuditChainCheckpointView?> GetLastCoveringAsync(
            AuditChainScope scope, long? to, CancellationToken ct) =>
            Task.FromResult(_cp is not null && (to is null || _cp.HeadSequence <= to) ? _cp : null);

        public Task<bool> VerifySignatureAsync(AuditChainCheckpointView checkpoint, CancellationToken ct) =>
            Task.FromResult(_signatureValid);

        public Task<long?> GetMaxHeadSequenceAsync(AuditChainScope scope, CancellationToken ct) =>
            Task.FromResult(_cp?.HeadSequence);
    }
}
