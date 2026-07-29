namespace Tamma.Data.Entities;

/// <summary>
/// A relation edge between two work items (Story 44-1 AC1 — table
/// <c>work_item_relations</c>, tenant-schema resident) over 44-0 AC14's
/// <c>WorkItemRelationKind</c> vocabulary (<c>blocks</c> | <c>duplicate</c> |
/// <c>related</c>, stored as the wire string).
///
/// <para><b>Canonical-form invariant.</b> Rows are stored in the form
/// <c>WorkItemRelationKindExtensions.Canonicalize</c> returns — symmetric
/// kinds (<c>duplicate</c>/<c>related</c>) with the LOWER id first
/// (<c>Guid.CompareTo</c> order), directed <c>blocks</c> unchanged
/// (source→target is meaning, not storage order). The unique index on
/// <c>(SourceId, TargetId, Kind)</c> ASSUMES this form: a mirror duplicate of
/// a symmetric edge maps onto the same stored row and collides. The
/// repository (<c>WorkItemRepository.AddRelationAsync</c>) is the only writer
/// and calls the shipped <c>Canonicalize</c> — never a reimplementation.</para>
///
/// <para>Enforcement beyond no-self-edge (a DB CHECK backing
/// <c>Canonicalize</c>'s <c>TRACKER.SELF_RELATION</c>) — no cross-project
/// edge, and deliberately NO cycle detection (a blocking cycle is a real
/// situation to show, not to prevent) — is Story 44-3's.</para>
/// </summary>
public class WorkItemRelation
{
    public Guid Id { get; set; }

    /// <summary>For <c>blocks</c>: the blocking item. For symmetric kinds: the lower id.</summary>
    public Guid SourceId { get; set; }

    /// <summary>For <c>blocks</c>: the blocked item. For symmetric kinds: the higher id.</summary>
    public Guid TargetId { get; set; }

    /// <summary><c>WorkItemRelationKind</c> wire string (blocks|duplicate|related).</summary>
    public string Kind { get; set; } = null!;

    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}
