namespace Tamma.Core.Audit;

/// <summary>
/// Story 37-2 — the immutable, storage-agnostic projection of an
/// <c>audit_records</c> row that the canonicalizer + verifier operate on.
///
/// <para><b>Why a view, not the EF entity:</b> <c>Tamma.Core</c> is a pure
/// project with no EF dependency (the chain's determinism must not ride on any
/// ORM behaviour). The <c>AuditRecord</c> entity in <c>Tamma.Data</c> maps into
/// this view; the canonicalizer never sees the entity.</para>
///
/// <para>Every field here is part of the hashed identity of the record EXCEPT
/// <see cref="PrevRecordHash"/> and <see cref="RecordHash"/> — those are the
/// chain linkage, composed AROUND the canonical bytes, not inside them.</para>
/// </summary>
public sealed record AuditChainRecordView
{
    public required Guid Id { get; init; }
    public required string Discriminator { get; init; }
    public Guid? TenantId { get; init; }
    public Guid? UserId { get; init; }
    public required string ActionCode { get; init; }
    public required string Category { get; init; }
    public required string Severity { get; init; }
    public Guid? ActorUserId { get; init; }
    public string? ActorEmailSnapshot { get; init; }
    public string? TargetType { get; init; }
    public string? TargetId { get; init; }
    public required string Outcome { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public required DateTime OccurredAt { get; init; }
    public required Guid SourceEventId { get; init; }
    public required long SourceSequenceNumber { get; init; }
    public required string PayloadJson { get; init; }

    /// <summary>Per-scope monotonic chain position (1-based). Part of the hashed identity.</summary>
    public required long ChainSequence { get; init; }

    /// <summary>The previous record's hash (lowercase-hex) — the chain link. NOT hashed into the canonical.</summary>
    public required string PrevRecordHash { get; init; }

    /// <summary>The stored hash (lowercase-hex) the verifier recomputes and compares against.</summary>
    public required string RecordHash { get; init; }
}
