using System.Text.Json;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Documents;

/// <summary>
/// Story 39-11 (Design Decisions D7/D8) — the pure, static mapper from
/// <c>Tamma.Data</c> <see cref="DocumentInstance"/> rows onto the shared
/// <c>Tamma.Core</c> lineage DTOs. It lives in <c>Tamma.Api</c> (not Core) because
/// it bridges the Data entity → Core DTO boundary, and Core cannot reference Data.
///
/// <para><b>Review linkage (D8).</b> A Review's subject resolves parent-first
/// (envelope <c>parent_document_id</c>), then a tolerant body probe
/// (<see cref="ResolveReviewSubject"/>). A review that resolves to no in-response
/// subject lands in <c>unlinkedReviews</c> — surfaced, never dropped.</para>
///
/// <para><b>Corruption tripwire (D5).</b> Every stored body is re-parsed and
/// re-validated against its type; a stored-invalid body (should be unreachable —
/// the write path validated it) throws <c>DOCUMENT.STORE.CORRUPT_BODY</c> rather
/// than handing out an un-typed blob.</para>
/// </summary>
public static class LineageAssembler
{
    private static readonly string ReviewTypeWire = DocumentTypeKey.Review.ToWire();
    private static readonly string SupersededWire = DocumentInstanceStatus.Superseded.ToWire();
    private static readonly string AcceptedWire = DocumentInstanceStatus.Accepted.ToWire();
    private static readonly string EscalatedWire = DocumentInstanceStatus.Escalated.ToWire();

    /// <summary>
    /// Assemble the full lineage response (AC3). Types are grouped in first-produced
    /// order, revisions ascending; reviews attach to their subject; the terminal
    /// outcome is derived from row statuses (never events, per 39-8's split).
    /// </summary>
    public static IssueDocumentLineage Assemble(string issueId, IReadOnlyList<DocumentInstance> rows)
    {
        var reviews = new List<DocumentInstance>();
        var subjects = new List<DocumentInstance>();
        foreach (var r in rows)
        {
            if (string.Equals(r.DocumentType, ReviewTypeWire, StringComparison.Ordinal))
                reviews.Add(r);
            else
                subjects.Add(r);
        }

        // Build review entries + resolve each review's subject id (parent-first,
        // then body probe). Group by resolved subject id.
        var reviewsBySubject = new Dictionary<Guid, List<LineageDocumentEntry>>();
        var unresolvedReviewIds = new HashSet<Guid>();
        var reviewEntries = new Dictionary<Guid, LineageDocumentEntry>();
        var reviewSubject = new Dictionary<Guid, Guid?>();
        foreach (var review in reviews)
        {
            var entry = ToEntry(review, Array.Empty<LineageDocumentEntry>());
            reviewEntries[review.Id] = entry;
            var subjectId = review.ParentDocumentId ?? ResolveReviewSubject(entry.Body);
            reviewSubject[review.Id] = subjectId;
            if (subjectId is Guid sid)
            {
                if (!reviewsBySubject.TryGetValue(sid, out var list))
                    reviewsBySubject[sid] = list = new List<LineageDocumentEntry>();
                list.Add(entry);
            }
            else
            {
                unresolvedReviewIds.Add(review.Id);
            }
        }

        // Build subject entries (attaching their reviews) and group by type in
        // first-produced order, revisions ascending.
        var typeOrder = new List<string>();
        var byType = new Dictionary<string, List<LineageDocumentEntry>>(StringComparer.Ordinal);
        var subjectIds = new HashSet<Guid>(subjects.Select(s => s.Id));
        foreach (var subject in subjects)
        {
            var attached = reviewsBySubject.TryGetValue(subject.Id, out var revs)
                ? (IReadOnlyList<LineageDocumentEntry>)revs
                : Array.Empty<LineageDocumentEntry>();
            var entry = ToEntry(subject, attached);
            if (!byType.TryGetValue(subject.DocumentType, out var trail))
            {
                byType[subject.DocumentType] = trail = new List<LineageDocumentEntry>();
                typeOrder.Add(subject.DocumentType);
            }
            trail.Add(entry);
        }

        var types = typeOrder
            .Select(t => new DocumentTypeTrail(
                t, byType[t].OrderBy(e => e.Revision).ToList()))
            .ToList();

        // Any review whose resolved subject is NOT a subject row in this response
        // (or resolved to nothing) is surfaced, never dropped (D8).
        var unlinked = reviews
            .Where(r => unresolvedReviewIds.Contains(r.Id)
                || (reviewSubject[r.Id] is Guid sid && !subjectIds.Contains(sid)))
            .Select(r => reviewEntries[r.Id])
            .ToList();

        return new IssueDocumentLineage(issueId, types, unlinked, DeriveOutcome(rows, types));
    }

    /// <summary>
    /// Assemble the latest-accepted-per-type response (AC4). Input rows are already
    /// the latest-accepted set (repository <c>GetLatestAcceptedAsync</c>).
    /// </summary>
    public static LatestAcceptedDocuments AssembleLatest(
        string issueId, IReadOnlyList<DocumentInstance> rows)
    {
        var docs = rows
            .Select(r => ToEntry(r, Array.Empty<LineageDocumentEntry>()))
            .OrderBy(e => e.CreatedAt)
            .ToList();
        return new LatestAcceptedDocuments(issueId, docs);
    }

    /// <summary>
    /// Project a single stored row to a <see cref="LineageDocumentEntry"/> (no
    /// attached reviews) for the bare-id fetch (AC5). Re-validates the body (D5).
    /// </summary>
    public static LineageDocumentEntry AssembleDocument(DocumentInstance row) =>
        ToEntry(row, Array.Empty<LineageDocumentEntry>());

    /// <summary>
    /// Outcome (D-terminal): <c>escalated</c> if any non-superseded row is
    /// escalated; <c>accepted</c> if there is ≥1 type and every type's latest
    /// revision is accepted; else <c>in-progress</c>.
    /// </summary>
    private static string DeriveOutcome(
        IReadOnlyList<DocumentInstance> rows, IReadOnlyList<DocumentTypeTrail> types)
    {
        if (rows.Any(r => !string.Equals(r.Status, SupersededWire, StringComparison.Ordinal)
                && string.Equals(r.Status, EscalatedWire, StringComparison.Ordinal)))
            return "escalated";

        if (types.Count > 0 && types.All(t =>
        {
            var latest = t.Revisions[^1]; // revisions are ascending
            return string.Equals(latest.Status, AcceptedWire, StringComparison.Ordinal);
        }))
            return "accepted";

        return "in-progress";
    }

    /// <summary>
    /// Tolerant body probe for a Review's subject reference (39-4's Review shape:
    /// <c>{ "subject": { "documentId": "&lt;guid&gt;" } }</c>). Returns null when
    /// absent or unparseable — the review is not dropped, it is surfaced unlinked.
    /// </summary>
    internal static Guid? ResolveReviewSubject(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        if (!body.TryGetProperty("subject", out var subject) || subject.ValueKind != JsonValueKind.Object)
            return null;
        if (!subject.TryGetProperty("documentId", out var docId) || docId.ValueKind != JsonValueKind.String)
            return null;
        return Guid.TryParse(docId.GetString(), out var g) ? g : null;
    }

    private static LineageDocumentEntry ToEntry(DocumentInstance row, IReadOnlyList<LineageDocumentEntry> reviews)
    {
        var body = ParseAndRevalidate(row);
        return new LineageDocumentEntry(
            row.Id,
            row.DocumentType,
            row.IssueId,
            row.ProducedByRole,
            row.ProducedByAction,
            row.Revision,
            row.Status,
            row.SupersedesDocumentId,
            row.ParentDocumentId,
            row.CorrelatingEventId,
            new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc)),
            body,
            reviews);
    }

    /// <summary>
    /// Re-parse + re-validate the stored body (D5 corruption tripwire). A stored
    /// body that does not parse or fails its type's validation throws
    /// <c>DOCUMENT.STORE.CORRUPT_BODY</c> — the "typed edges" note enforced on read.
    /// </summary>
    private static JsonElement ParseAndRevalidate(DocumentInstance row)
    {
        JsonElement body;
        try
        {
            using var doc = JsonDocument.Parse(row.BodyJson);
            body = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw Corrupt(row, ex.Message);
        }

        var validation = DocumentTypeRegistry.Resolve(row.DocumentType).Validate(body);
        if (!validation.IsValid)
            throw Corrupt(row, string.Join("; ", validation.Violations.Select(v => v.Code)));

        return body;
    }

    private static TammaError Corrupt(DocumentInstance row, string detail) => new(
        "DOCUMENT.STORE.CORRUPT_BODY",
        $"Stored document '{row.Id}' ({row.DocumentType}) has an invalid body — the store " +
        $"must never hand out an un-typed blob (stream wins; re-project). Detail: {detail}",
        new Dictionary<string, object?>
        {
            ["documentId"] = row.Id,
            ["type"] = row.DocumentType,
        },
        retryable: false,
        severity: TammaErrorSeverity.Critical);
}
