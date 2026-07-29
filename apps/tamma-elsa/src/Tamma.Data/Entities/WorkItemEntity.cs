namespace Tamma.Data.Entities;

/// <summary>
/// A native work item (Story 44-1 AC1 — table <c>work_items</c>, tenant-schema
/// resident; the highest-row-count operational tenant table). Vocabulary
/// columns (<see cref="Kind"/>, <see cref="Status"/>, <see cref="Priority"/>,
/// <see cref="IssueType"/>) store 44-0's WIRE STRINGS; the DB CHECK
/// constraints are the enforcement and the <c>Tamma.Core.Tracking</c>
/// extensions are the parse boundary — exactly the
/// <c>DocumentInstance.Status</c> posture.
///
/// <para><b>The key is frozen at creation (44-0 AC8 / epic D13).</b>
/// <see cref="Key"/> is minted once from the creating project's sequence
/// (<c>WorkItemRef.ToWire()</c>) and NEVER re-minted — including on a move to
/// another project, after which the prefix no longer matches the project and
/// that is intended. The key is already written into
/// <c>DocumentInstance.IssueId</c> and DCB <c>tags.issueId</c> (append-only),
/// so a re-mint would orphan the item's document lineage and event history
/// silently. The one sanctioned exception — a deliberate operator re-key —
/// appends the outgoing key to <see cref="PreviousKeys"/> via
/// <c>WorkItemKeyHistory.Record</c>, and lookup by key resolves
/// current-or-previous.</para>
///
/// <para><b>Two rank axes, one algebra (44-0 AC10).</b> <see cref="Rank"/> is
/// the flat project-backlog position; <see cref="SiblingRank"/> is the
/// position among siblings under the same parent (null parent included). Both
/// columns are created <c>COLLATE "C"</c> — the storage obligation
/// <c>Rank.cs</c> states: the base-62 alphabet agrees with Postgres
/// <c>ORDER BY</c> only under the <c>C</c> collation.</para>
///
/// <para>No principal XOR / no <c>TenantId</c> column: a work item is CONTENT
/// inside one tenant schema, not per-principal configuration (epic D6).</para>
/// </summary>
public class WorkItemEntity
{
    /// <summary>UUIDv7 (client-set via <c>UuidV7.NewGuid()</c> at create).</summary>
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>The frozen wire key, e.g. <c>TAM-142</c>. Unique per tenant.</summary>
    public string Key { get; set; } = null!;

    /// <summary>
    /// 44-0 AC8 — outgoing keys from deliberate operator re-keys, oldest
    /// first, empty by default. Written ONLY through
    /// <c>WorkItemKeyHistory.Record</c>; a project move alone records nothing.
    /// </summary>
    public List<string> PreviousKeys { get; set; } = [];

    /// <summary>The per-project sequence number the key was minted from (≥ 1).</summary>
    public int Number { get; set; }

    /// <summary>44-0 AC1 — <c>WorkItemKind</c> wire string (epic|story|task|spike).</summary>
    public string Kind { get; set; } = null!;

    /// <summary>44-0 AC2 — <c>WorkItemStatus</c> wire string (8 members, triage included).</summary>
    public string Status { get; set; } = null!;

    /// <summary>
    /// 44-0 AC11 — <c>TriagePriority</c> wire string, NULLABLE. <c>null</c> is
    /// "nobody has prioritised this", a different fact from <c>normal</c>.
    /// </summary>
    public string? Priority { get; set; }

    /// <summary>
    /// 44-0 AC12 — <c>TriageIssueType</c> wire string (bug|feature|chore|
    /// question|security|docs). Nullable: an imported or triaged item exists
    /// before anyone classified it and the vocabulary has no "unset" member.
    /// </summary>
    public string? IssueType { get; set; }

    public string Title { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>Self-FK, RESTRICT on delete (44-2 returns 409; no silent subtree loss).</summary>
    public Guid? ParentId { get; set; }

    /// <summary>FK to <c>iterations</c>, SET NULL on delete. Populated by 44-4.</summary>
    public Guid? IterationId { get; set; }

    /// <summary>Flat project-backlog position. <c>COLLATE "C"</c> (44-0 D7).</summary>
    public string Rank { get; set; } = null!;

    /// <summary>Order among siblings under <see cref="ParentId"/>. <c>COLLATE "C"</c>.</summary>
    public string SiblingRank { get; set; } = null!;

    public Guid? AssigneeUserId { get; set; }
    public Guid? CreatedByUserId { get; set; }

    /// <summary>
    /// 44-0 AC13 — scale-free estimate (<c>numeric NULL</c>). The scale lives
    /// on <see cref="ProjectEntity.EstimateScale"/>; this is deliberately NOT
    /// <c>EstimateHours</c>. Nothing reads it in v1 (Epic 36 owns velocity);
    /// it is stored so the history exists when something does.
    /// </summary>
    public decimal? Estimate { get; set; }

    /// <summary>
    /// One <c>jsonb</c> column for the external-platform link; 44-8 owns its
    /// shape (<c>platformKind</c>/<c>repoFullName</c>/<c>number</c>/<c>url</c>).
    /// NULL for native items.
    /// </summary>
    public string? ExternalRefJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stamped when <see cref="Status"/> becomes terminal; cleared on reopen.</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>Optimistic-concurrency counter, bumped on every write (44-2's ETag).</summary>
    public int Version { get; set; } = 1;
}
