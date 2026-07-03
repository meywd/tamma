namespace Tamma.Core.Audit;

/// <summary>
/// Story 37-2 (AC5) — a storage-agnostic view of one signed chain checkpoint: a
/// cabinet-key-signed anchor of a chain head at a point in time. Used by the
/// verifier to confirm the recomputed head matches the signed anchor.
/// </summary>
public sealed record AuditChainCheckpointView
{
    public required Guid Id { get; init; }
    public required string Scope { get; init; }
    public Guid? TenantId { get; init; }
    public required long HeadSequence { get; init; }

    /// <summary>The chain head hash (lowercase-hex) this checkpoint anchors.</summary>
    public required string HeadHash { get; init; }

    public required DateTime SignedAt { get; init; }

    /// <summary>The HMAC signature bytes over the canonical checkpoint preimage.</summary>
    public required byte[] Signature { get; init; }

    /// <summary>Which cabinet signing-key version produced <see cref="Signature"/>.</summary>
    public required int KeyVersion { get; init; }
}
