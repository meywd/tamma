namespace Tamma.Core.Audit;

/// <summary>
/// Story 37-2 (AC4) — walks a scope's hash-chain and reports OK or the first
/// broken link. Pure logic (no EF/HTTP); data access is behind
/// <see cref="IAuditChainRecordSource"/> / <see cref="IAuditChainCheckpointGateway"/>.
/// </summary>
public interface IAuditChainVerifier
{
    /// <summary>
    /// Verify the chain for <paramref name="scope"/> over the inclusive
    /// <c>chain_sequence</c> range <c>[from, to]</c> (null = open bound; the
    /// default range is genesis→head). Confirms per-record hash integrity,
    /// <c>prev_hash</c> linkage, and <c>chain_sequence</c> contiguity, then
    /// validates any covering checkpoint's signature + head hash.
    /// </summary>
    Task<ChainVerificationResult> VerifyAsync(
        AuditChainScope scope, long? from, long? to, CancellationToken ct);
}
