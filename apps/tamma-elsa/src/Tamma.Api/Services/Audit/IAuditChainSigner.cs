using Tamma.Core.Audit;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-2 (AC5) — signs / verifies audit-chain checkpoint anchors using a
/// key from the Epic 29 secret cabinet. Fail-closed: signing throws and
/// verification returns false when the key is unavailable — a missing key never
/// silently produces an "unsigned but valid-looking" anchor.
/// </summary>
public interface IAuditChainSigner
{
    /// <summary>
    /// Sign the canonical preimage of a checkpoint anchor. Returns the signature
    /// bytes and the cabinet <c>key_version</c> that produced them (so rotation
    /// does not strand historical checkpoints). Throws when no signing key is
    /// available.
    /// </summary>
    Task<(byte[] Signature, int KeyVersion)> SignAsync(
        string scope, Guid? tenantId, long headSequence, string headHashHex,
        DateTime signedAt, CancellationToken ct = default);

    /// <summary>
    /// Validate a persisted checkpoint's signature against the cabinet key for
    /// its <c>key_version</c>. Returns false on any mismatch OR when the key is
    /// unavailable (fail-closed).
    /// </summary>
    Task<bool> VerifyAsync(AuditChainCheckpointView checkpoint, CancellationToken ct = default);
}
