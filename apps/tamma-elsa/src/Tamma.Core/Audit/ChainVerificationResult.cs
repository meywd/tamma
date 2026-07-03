namespace Tamma.Core.Audit;

/// <summary>
/// Story 37-2 (AC4/AC7) — why a chain verification failed, localized to the
/// exact broken link.
/// </summary>
public enum ChainBreakReason
{
    /// <summary>A record's recomputed hash != its stored hash (in-place edit).</summary>
    Mutated,

    /// <summary>A gap in <c>chain_sequence</c> (a record was deleted / is missing).</summary>
    Missing,

    /// <summary>Records appear out of sequence order (reordering).</summary>
    Reordered,

    /// <summary>A record's <c>prev_hash</c> != the actual prior record's hash.</summary>
    PrevHashMismatch,

    /// <summary>A checkpoint's stored signature failed cabinet-key validation.</summary>
    CheckpointSignatureInvalid,

    /// <summary>A checkpoint's <c>head_hash</c> != the recomputed chain head at its sequence.</summary>
    CheckpointHeadMismatch,
}

/// <summary>The status half of a <see cref="ChainVerificationResult"/>.</summary>
public enum ChainVerificationStatus
{
    /// <summary>Every link verified; the chain (and any covering checkpoint) is intact.</summary>
    Ok,

    /// <summary>A tamper was detected; see <see cref="ChainVerificationResult.FirstBrokenLink"/>.</summary>
    Tampered,
}

/// <summary>
/// Story 37-2 (AC4) — the first broken link in a chain: enough to localize
/// tampering without leaking record payload contents.
/// </summary>
public sealed record ChainBrokenLink(
    Guid? RecordId,
    long ChainSequence,
    ChainBreakReason Reason);

/// <summary>
/// Story 37-2 (AC4/AC7) — the structured outcome of verifying a chain range:
/// either <see cref="ChainVerificationStatus.Ok"/> or the first broken link.
/// Also carries the head reached and the covering checkpoint (if any) so the
/// endpoint can surface <c>lastCheckpoint</c>.
/// </summary>
public sealed record ChainVerificationResult
{
    public required ChainVerificationStatus Status { get; init; }

    /// <summary>Set only when <see cref="Status"/> is <see cref="ChainVerificationStatus.Tampered"/>.</summary>
    public ChainBrokenLink? FirstBrokenLink { get; init; }

    /// <summary>The last (highest) <c>chain_sequence</c> the verify reached. 0 for an empty range.</summary>
    public long LastSequence { get; init; }

    /// <summary>The last verified record's hash (hex), or null for an empty range.</summary>
    public string? LastHash { get; init; }

    /// <summary>The covering checkpoint that was confirmed (or attempted), if one exists.</summary>
    public AuditChainCheckpointView? LastCheckpoint { get; init; }

    /// <summary>Number of records walked (for logging / perf assertions).</summary>
    public long RecordsVerified { get; init; }

    public static ChainVerificationResult Ok(
        long lastSequence, string? lastHash, long recordsVerified,
        AuditChainCheckpointView? checkpoint) =>
        new()
        {
            Status = ChainVerificationStatus.Ok,
            LastSequence = lastSequence,
            LastHash = lastHash,
            RecordsVerified = recordsVerified,
            LastCheckpoint = checkpoint,
        };

    public static ChainVerificationResult Broken(
        ChainBreakReason reason, Guid? recordId, long chainSequence,
        long recordsVerified, AuditChainCheckpointView? checkpoint = null) =>
        new()
        {
            Status = ChainVerificationStatus.Tampered,
            FirstBrokenLink = new ChainBrokenLink(recordId, chainSequence, reason),
            LastSequence = chainSequence,
            RecordsVerified = recordsVerified,
            LastCheckpoint = checkpoint,
        };
}
