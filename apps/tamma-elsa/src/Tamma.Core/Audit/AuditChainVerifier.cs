namespace Tamma.Core.Audit;

/// <summary>
/// Story 37-2 (AC4/AC7) — the pure chain-verification algorithm. Streams a
/// scope's records in ascending <c>chain_sequence</c>, and for each one:
/// <list type="number">
///   <item><description>checks <c>chain_sequence</c> contiguity (gap → missing;
///     backwards → reordered);</description></item>
///   <item><description>checks <c>prev_hash</c> == the prior record's hash;</description></item>
///   <item><description>recomputes <c>SHA-256(prev ‖ canonical(record))</c> and
///     compares it to the stored <c>record_hash</c> (mismatch → mutated).</description></item>
/// </list>
/// Then it confirms any covering checkpoint's signature + anchored head hash.
/// Returns at the FIRST broken link, localized to its <c>chain_sequence</c>.
///
/// <para>O(n) over the range and streaming (never materializes the whole chain)
/// so a 100k-record verify stays within budget (AC12).</para>
/// </summary>
public sealed class AuditChainVerifier : IAuditChainVerifier
{
    private readonly IAuditChainRecordSource _records;
    private readonly IAuditChainCheckpointGateway _checkpoints;

    public AuditChainVerifier(
        IAuditChainRecordSource records,
        IAuditChainCheckpointGateway checkpoints)
    {
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
    }

    public async Task<ChainVerificationResult> VerifyAsync(
        AuditChainScope scope, long? from, long? to, CancellationToken ct)
    {
        // The checkpoint covering this range — fetched up front so we can capture
        // the record hash AT its head sequence while streaming (single pass).
        var checkpoint = await _checkpoints.GetLastCoveringAsync(scope, to, ct)
            .ConfigureAwait(false);

        // Anchor the expected prev_hash: genesis when starting at/ before the
        // first record; otherwise the hash of the record just before `from`.
        string? expectedPrev;
        if (from is null || from.Value <= 1)
        {
            expectedPrev = AuditChainGenesis.HashHex;
        }
        else
        {
            expectedPrev = await _records.GetRecordHashAtAsync(scope, from.Value - 1, ct)
                .ConfigureAwait(false);
            // Null anchor → the record before the range is missing; we cannot
            // confirm the first record's prev linkage, so skip that one check
            // (the hash-recompute + contiguity still apply).
        }

        long? expectedSeq = from;
        long lastSeq = 0;
        string? lastHash = null;
        long verified = 0;
        string? hashAtCheckpointHead = null;

        await foreach (var r in _records.StreamAsync(scope, from, to, ct).ConfigureAwait(false))
        {
            // ── contiguity ──
            if (expectedSeq is long es && r.ChainSequence != es)
            {
                if (r.ChainSequence > es)
                {
                    // A slot in the middle is empty → a record was deleted.
                    return ChainVerificationResult.Broken(
                        ChainBreakReason.Missing, r.Id, es, verified, checkpoint);
                }

                // Sequence went backwards / duplicated → reordering.
                return ChainVerificationResult.Broken(
                    ChainBreakReason.Reordered, r.Id, r.ChainSequence, verified, checkpoint);
            }

            // ── prev linkage ──
            if (expectedPrev is not null &&
                !string.Equals(r.PrevRecordHash, expectedPrev, StringComparison.OrdinalIgnoreCase))
            {
                return ChainVerificationResult.Broken(
                    ChainBreakReason.PrevHashMismatch, r.Id, r.ChainSequence, verified, checkpoint);
            }

            // ── content integrity ──
            var recomputed = AuditChainHasher.ComposeHex(
                r.PrevRecordHash, AuditRecordCanonicalizer.ToBytes(r));
            if (!string.Equals(recomputed, r.RecordHash, StringComparison.OrdinalIgnoreCase))
            {
                return ChainVerificationResult.Broken(
                    ChainBreakReason.Mutated, r.Id, r.ChainSequence, verified, checkpoint);
            }

            if (checkpoint is not null && r.ChainSequence == checkpoint.HeadSequence)
            {
                hashAtCheckpointHead = r.RecordHash;
            }

            expectedPrev = r.RecordHash;
            expectedSeq = r.ChainSequence + 1;
            lastSeq = r.ChainSequence;
            lastHash = r.RecordHash;
            verified++;
        }

        // ── checkpoint confirmation (AC7) ──
        if (checkpoint is not null)
        {
            if (!await _checkpoints.VerifySignatureAsync(checkpoint, ct).ConfigureAwait(false))
            {
                return ChainVerificationResult.Broken(
                    ChainBreakReason.CheckpointSignatureInvalid,
                    checkpoint.Id, checkpoint.HeadSequence, verified, checkpoint);
            }

            var headInRange =
                (from is null || from.Value <= checkpoint.HeadSequence) &&
                (to is null || to.Value >= checkpoint.HeadSequence);

            if (headInRange)
            {
                if (hashAtCheckpointHead is null)
                {
                    // The range should have contained the anchored head but the
                    // record at that sequence is absent → tail clipped / missing.
                    return ChainVerificationResult.Broken(
                        ChainBreakReason.Missing, null, checkpoint.HeadSequence, verified, checkpoint);
                }

                if (!string.Equals(hashAtCheckpointHead, checkpoint.HeadHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return ChainVerificationResult.Broken(
                        ChainBreakReason.CheckpointHeadMismatch,
                        checkpoint.Id, checkpoint.HeadSequence, verified, checkpoint);
                }
            }
        }

        return ChainVerificationResult.Ok(lastSeq, lastHash, verified, checkpoint);
    }
}
