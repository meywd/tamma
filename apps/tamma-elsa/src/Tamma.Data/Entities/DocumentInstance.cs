namespace Tamma.Data.Entities;

/// <summary>
/// Story 39-11 — one persisted work-document instance (one revision) in the
/// tenant-resident <c>document_instances</c> table.
///
/// <para><b>This is a read-optimized product layer over the DCB stream, NOT a new
/// event store.</b> Every lifecycle transition already emits a <c>DOCUMENT.*</c>
/// event (39-6); this table is the queryable document PRODUCT built in the same
/// operation flow. It is rebuildable at any time (truncate + re-write from the
/// events), and if the store and the stream ever disagree, the STREAM WINS
/// (Story 37-1's "truncate + re-project" doctrine). Each row back-references the
/// correlating transition event via <see cref="CorrelatingEventId"/> so an auditor
/// can cross-check store ↔ stream mechanically (AC7).</para>
///
/// <para>Envelope fields are COLUMNS (39-2 <see cref="Tamma.Core.Documents.DocumentEnvelope"/>);
/// the typed document body rides <see cref="BodyJson"/> as JSONB, validated by the
/// 39-3/39-4 type registry BEFORE write. Immutability is by API absence: the sole
/// writer (<c>IDocumentInstanceRepository</c>) offers no body-update and no delete —
/// a revise round inserts a NEW row (<c>revision+1</c>) and flips the prior row to
/// <c>superseded</c> (Design Decision D4).</para>
/// </summary>
public class DocumentInstance
{
    /// <summary>
    /// Primary key — the envelope's UUID v7 identity, set CLIENT-SIDE from the
    /// envelope (NO <c>gen_random_uuid()</c> default). The envelope id IS the row
    /// id, so the store row and the DCB event's <c>documentId</c> tag are the same
    /// value.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>The document type key — a <c>DocumentTypeKey</c> wire string.</summary>
    public string DocumentType { get; set; } = null!;

    /// <summary>The mandatory lineage anchor (39-2: a string DCB tag, non-nullable — D11).</summary>
    public string IssueId { get; set; } = null!;

    /// <summary>Producer provenance — the agent role wire string.</summary>
    public string ProducedByRole { get; set; } = null!;

    /// <summary>Producer provenance — the agent action wire string.</summary>
    public string ProducedByAction { get; set; } = null!;

    /// <summary>Producer provenance — the workflow definition id (kebab token).</summary>
    public string? ProducedByWorkflow { get; set; }

    /// <summary>The payload schema version this instance was validated against.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>The correlation id lineage anchor (how AC7's auditor pivots to the event stream).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>1-based revision within the supersession chain (D4).</summary>
    public int Revision { get; set; }

    /// <summary>
    /// The store status wire string (a <c>DocumentInstanceStatus</c> wire — CHECK
    /// constrained to the 7-value set, D3).
    /// </summary>
    public string Status { get; set; } = null!;

    /// <summary>The prior revision this row supersedes (null on revision 1). Self-FK (D4).</summary>
    public Guid? SupersedesDocumentId { get; set; }

    /// <summary>The subject document for a Review instance (how the lineage resolves it, D8).</summary>
    public Guid? ParentDocumentId { get; set; }

    /// <summary>
    /// The pre-minted <c>DOCUMENT.*</c> transition event id this write correlates to
    /// (AC7 linkage). Equals <c>domain_events."Id"</c> for the same transition.
    /// </summary>
    public Guid? CorrelatingEventId { get; set; }

    /// <summary>
    /// Transitional tenant predicate column (Doc 01 §1.4). The per-tenant schema is
    /// the real isolation plane; this carries the shared-DB phase and the
    /// defence-in-depth entity-level re-check.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Story 41-1c — the document's audience tag (a <c>ProseAudience</c> wire
    /// string; envelope <c>audience</c>). NULL for every non-prose document and
    /// for rows written before the tag existed; a prose row cannot be WRITTEN
    /// without one (enforced by <c>ProseDocumentType.Validate</c> at the
    /// repository's write door, never by a NOT NULL column — 41-1c D8).
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>The typed document body as JSONB (envelope payload raw JSON).</summary>
    public string BodyJson { get; set; } = "{}";

    /// <summary>When this revision was produced (envelope <c>createdAt</c>).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When this row was last touched (status transitions bump it).</summary>
    public DateTime UpdatedAt { get; set; }
}
