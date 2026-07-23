using System.Text.Json.Serialization;

namespace Tamma.Core.Documents.Resume;

/// <summary>
/// Story 39-10 (AC5) — the coarse lifecycle stage a re-entering instance resumes
/// at, computed by <see cref="LifecycleResumeCalculator"/> from the 39-11
/// latest-accepted read + the DCB event slice. Ordered from earliest to latest
/// stage.
/// </summary>
public enum LifecycleResumeStage
{
    /// <summary>No usable prior work — run the full lifecycle fresh (the today-behaviour default).</summary>
    Produce,

    /// <summary>A draft was produced + validated but not yet reviewed — skip produce/validate, review it.</summary>
    Review,

    /// <summary>The accept gate was reached (APPROVAL.REQUESTED) but no decision landed — re-suspend on the recovered session.</summary>
    Accept,

    /// <summary>The document of this type is already accepted — short-circuit to the accepted terminal.</summary>
    Complete,
}

/// <summary>
/// Story 39-10 (AC5) — the TYPED resume position for one document type on one
/// issue. Reconstructed from durable truth (document lineage + DCB events), NEVER
/// from Elsa instance internals. Every wire property carries an explicit
/// <c>[JsonPropertyName]</c> and is serialized with <see cref="DocumentJson.Options"/>.
/// </summary>
public sealed record LifecycleResumePosition
{
    [JsonPropertyName("documentTypeKey")]
    public required string DocumentTypeKey { get; init; }

    [JsonPropertyName("resumeAt")]
    public required LifecycleResumeStage ResumeAt { get; init; }

    /// <summary>The store/stream id of the revision to re-enter at (null for a fresh Produce).</summary>
    [JsonPropertyName("existingDocumentId")]
    public Guid? ExistingDocumentId { get; init; }

    /// <summary>The 1-based revision of <see cref="ExistingDocumentId"/> (null for a fresh Produce).</summary>
    [JsonPropertyName("existingRevision")]
    public int? ExistingRevision { get; init; }

    /// <summary>
    /// The decision-session id recovered from an unanswered <c>APPROVAL.REQUESTED</c>
    /// (only set for <see cref="LifecycleResumeStage.Accept"/> re-entry — the gate
    /// re-suspends on the SAME session bookmark so a pre-crash resume still lands).
    /// </summary>
    [JsonPropertyName("pendingDecisionSessionId")]
    public Guid? PendingDecisionSessionId { get; init; }

    /// <summary>Human-readable derivation (e.g. "Decomposition accepted" / "produced-but-unreviewed at revision 2").</summary>
    [JsonPropertyName("basis")]
    public required string Basis { get; init; }

    /// <summary>Convenience — a fresh Produce position (no prior usable work).</summary>
    public static LifecycleResumePosition Fresh(string documentTypeKey, string basis) => new()
    {
        DocumentTypeKey = documentTypeKey,
        ResumeAt = LifecycleResumeStage.Produce,
        Basis = basis,
    };
}

/// <summary>
/// Story 39-10 (D1) — a NEUTRAL event DTO the pure calculator folds over. Core
/// cannot see <c>Tamma.Data</c>'s <c>DomainEvent</c>, so the I/O service
/// (<c>LifecycleReEntryService</c>) maps the persisted rows onto this shape before
/// delegating. Carries only the fields the fold reads.
/// </summary>
public sealed record ResumeEventRow(
    string Type,
    DateTime CreatedAtUtc,
    Guid? DocumentId,
    string? DocumentTypeKey,
    Guid? SessionId,
    int? Revision);

/// <summary>
/// Story 39-10 (AC5) — the latest ACCEPTED document reference for one type (the
/// 39-11 <c>GetLatestAcceptedAsync</c> result mapped to a Core-visible shape).
/// <c>null</c> means "no accepted document of this type for this issue".
/// </summary>
public sealed record AcceptedDocumentRef(
    Guid DocumentId,
    string DocumentTypeKey,
    int Revision);
