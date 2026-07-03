namespace Tamma.Core.Audit;

/// <summary>
/// Story 37-2 — the read seam the pure <see cref="AuditChainVerifier"/> pulls
/// records from. Implemented in <c>Tamma.Data</c> over the correct store (CP for
/// <see cref="AuditChainScopeKind.Platform"/>, the tenant schema for
/// <see cref="AuditChainScopeKind.Tenant"/>). Streams in ascending
/// <c>chain_sequence</c> order and NEVER materializes the whole chain in memory
/// (AC4/AC12).
/// </summary>
public interface IAuditChainRecordSource
{
    /// <summary>
    /// Stream the scope's chained records with <c>chain_sequence</c> in
    /// <c>[from, to]</c> (inclusive; null = open bound), ascending. Only rows
    /// that carry a <c>chain_sequence</c> are returned (un-backfilled legacy
    /// rows are skipped by the source).
    /// </summary>
    IAsyncEnumerable<AuditChainRecordView> StreamAsync(
        AuditChainScope scope, long? from, long? to, CancellationToken ct);

    /// <summary>
    /// The <c>record_hash</c> (hex) of the record at <paramref name="sequence"/>
    /// in the scope, or null if none. Used to anchor <c>prev_hash</c> when a
    /// verify starts mid-chain (<c>from &gt; 1</c>).
    /// </summary>
    Task<string?> GetRecordHashAtAsync(
        AuditChainScope scope, long sequence, CancellationToken ct);

    /// <summary>
    /// The chain head — the highest <c>chain_sequence</c> and its
    /// <c>record_hash</c> — for the scope, or null when the chain is empty. Used
    /// by the checkpoint writer to anchor the current head.
    /// </summary>
    Task<AuditChainHead?> GetHeadAsync(AuditChainScope scope, CancellationToken ct);
}

/// <summary>Story 37-2 — the head of a chain: its highest sequence + hash.</summary>
public sealed record AuditChainHead(long Sequence, string RecordHash);

/// <summary>
/// Story 37-2 (AC5/AC7) — the checkpoint read + signature-validation seam.
/// Implemented in <c>Tamma.Api</c> (checkpoint rows are CP-resident; signature
/// validation uses the Epic 29 cabinet key).
/// </summary>
public interface IAuditChainCheckpointGateway
{
    /// <summary>
    /// The latest checkpoint whose <c>head_sequence &lt;= to</c> (or the latest
    /// overall when <paramref name="to"/> is null) for the scope, or null when
    /// no checkpoint covers the range.
    /// </summary>
    Task<AuditChainCheckpointView?> GetLastCoveringAsync(
        AuditChainScope scope, long? to, CancellationToken ct);

    /// <summary>
    /// Validate a checkpoint's signature against the cabinet key for its
    /// <c>key_version</c>. Fail-closed: returns false when the key is
    /// unavailable (never treats "no key" as "valid").
    /// </summary>
    Task<bool> VerifySignatureAsync(AuditChainCheckpointView checkpoint, CancellationToken ct);
}
