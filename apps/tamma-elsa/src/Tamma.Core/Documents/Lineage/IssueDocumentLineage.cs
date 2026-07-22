using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamma.Core.Documents;

/// <summary>
/// Story 39-11 (Design Decision D7) — the SHARED lineage response DTOs backing the
/// read endpoints (<c>GET /api/documents/issues/{issueId}/lineage</c> +
/// <c>/latest</c>). They live in <c>Tamma.Core</c> so 39-8's escalation payload can
/// serialize a slice of the same shapes without duplicating them (technical note).
///
/// <para><b>Naming.</b> The root record is <see cref="IssueDocumentLineage"/> —
/// deliberately NOT <c>DocumentLineage</c>, which 39-6 already owns for its in-run
/// drafts/reviews/rounds record. The two are different granularities and must not
/// collide. Every wire property carries an explicit <c>[JsonPropertyName]</c> and
/// serializes through <see cref="DocumentJson.Options"/> (39-2 D8 discipline).</para>
/// </summary>
public sealed record LineageDocumentEntry(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("documentType")] string DocumentType,
    [property: JsonPropertyName("issueId")] string IssueId,
    [property: JsonPropertyName("producedByRole")] string ProducedByRole,
    [property: JsonPropertyName("producedByAction")] string ProducedByAction,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("supersedesDocumentId")] Guid? SupersedesDocumentId,
    [property: JsonPropertyName("parentDocumentId")] Guid? ParentDocumentId,
    [property: JsonPropertyName("correlatingEventId")] Guid? CorrelatingEventId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("body")] JsonElement Body,
    [property: JsonPropertyName("reviews")] IReadOnlyList<LineageDocumentEntry> Reviews);

/// <summary>
/// One document type's revision trail (all revisions ascending), grouped in
/// first-produced order within the issue.
/// </summary>
public sealed record DocumentTypeTrail(
    [property: JsonPropertyName("documentType")] string DocumentType,
    [property: JsonPropertyName("revisions")] IReadOnlyList<LineageDocumentEntry> Revisions);

/// <summary>
/// The full document trail for an issue (AC3): every type's revision trail, any
/// reviews that could not be attached to a subject in-response
/// (<see cref="UnlinkedReviews"/> — surfaced, never dropped, D8), and the terminal
/// <see cref="Outcome"/> (<c>"accepted"</c> | <c>"escalated"</c> |
/// <c>"in-progress"</c>).
/// </summary>
public sealed record IssueDocumentLineage(
    [property: JsonPropertyName("issueId")] string IssueId,
    [property: JsonPropertyName("types")] IReadOnlyList<DocumentTypeTrail> Types,
    [property: JsonPropertyName("unlinkedReviews")] IReadOnlyList<LineageDocumentEntry> UnlinkedReviews,
    [property: JsonPropertyName("outcome")] string Outcome);

/// <summary>
/// The latest-accepted-per-type state for an issue (AC4) — exactly the read
/// 39-10's re-entry consumes. At most one entry per document type; superseded and
/// draft revisions never appear.
/// </summary>
public sealed record LatestAcceptedDocuments(
    [property: JsonPropertyName("issueId")] string IssueId,
    [property: JsonPropertyName("documents")] IReadOnlyList<LineageDocumentEntry> Documents);
