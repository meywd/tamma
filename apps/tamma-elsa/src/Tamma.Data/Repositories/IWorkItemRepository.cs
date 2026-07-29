using Tamma.Core.Tracking;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Filter + keyset-paging query for <see cref="IWorkItemRepository.ListAsync"/>.
/// Ordering is always <c>(Rank, Key)</c> ascending — the DB's <c>COLLATE "C"</c>
/// rank order IS the API order (Story 44-1 AC4); there is no application-side
/// sort. <see cref="AfterRank"/>/<see cref="AfterKey"/> are the keyset cursor
/// (the last row of the previous page); both-null = first page.
///
/// <para><b>Deliberately carries NO principal plane</b> — no <c>UserId</c> /
/// <c>TenantId</c> ownership filter exists on any work-item surface (story
/// AC7 / epic D6; pinned by <c>TrackerOwnershipTests</c>).
/// <see cref="AssigneeUserId"/> is assignment, not ownership.</para>
/// </summary>
public sealed record WorkItemQuery
{
    public Guid? ProjectId { get; init; }

    /// <summary><c>WorkItemStatus</c> wire strings; null/empty = all.</summary>
    public IReadOnlyCollection<string>? Statuses { get; init; }

    /// <summary><c>WorkItemKind</c> wire strings; null/empty = all.</summary>
    public IReadOnlyCollection<string>? Kinds { get; init; }

    public Guid? AssigneeUserId { get; init; }
    public Guid? IterationId { get; init; }

    /// <summary>Filter to children of this parent. See <see cref="TopLevelOnly"/> for null-parent.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>True = only items with no parent (top-level).</summary>
    public bool TopLevelOnly { get; init; }

    /// <summary>Case-insensitive substring match over the title.</summary>
    public string? TitleContains { get; init; }

    public string? AfterRank { get; init; }
    public string? AfterKey { get; init; }
    public int Limit { get; init; } = 100;
}

/// <summary>
/// Persistence seam for <c>work_items</c> + <c>work_item_relations</c>
/// (Story 44-1). Tenant-schema resident; <b>no mode split</b> (story AC7 /
/// epic D6 — a work item is content, and no method here filters on a
/// principal ownership plane).
///
/// <para><b>Key minting (AC5):</b> <see cref="CreateAsync"/> mints
/// <c>(Number, Key)</c> from the project's <c>NextNumber</c> counter under a
/// <c>SELECT ... FOR UPDATE</c> row lock inside the create transaction —
/// gap-free, monotone, concurrency-safe. The key string is
/// <c>WorkItemRef.ToWire()</c> and is FROZEN from that moment (44-0 AC8).</para>
///
/// <para><b>Key history:</b> <see cref="RekeyAsync"/> is the one sanctioned
/// exception — it records the outgoing key via
/// <c>WorkItemKeyHistory.Record</c>, and <see cref="GetByKeyAsync"/> resolves
/// current-or-previous so already-written <c>DocumentInstance.IssueId</c> and
/// DCB tags keep finding the item.</para>
///
/// <para><b>Relations:</b> the relation writers are the ONLY writers of
/// <c>work_item_relations</c> and always store the
/// <c>WorkItemRelationKindExtensions.Canonicalize</c> form (symmetric kinds
/// lower-id-first), which the unique index assumes.</para>
/// </summary>
public interface IWorkItemRepository
{
    Task<WorkItemEntity?> GetAsync(Guid id);

    /// <summary>
    /// Resolve a wire key (<c>TAM-142</c>) to its item — the CURRENT key or
    /// any recorded previous key (44-0 AC8; ordinal, never normalized).
    /// </summary>
    Task<WorkItemEntity?> GetByKeyAsync(string key);

    /// <summary>List per <see cref="WorkItemQuery"/>, ordered <c>(Rank, Key)</c>, keyset-paged.</summary>
    Task<List<WorkItemEntity>> ListAsync(WorkItemQuery query);

    /// <summary>
    /// Create a work item. Mints <c>Number</c>/<c>Key</c> under the project
    /// row lock; assigns a UUIDv7 id when unset; appends <c>Rank</c> /
    /// <c>SiblingRank</c> via <c>Rank.Append</c> over the current maxima when
    /// unset; validates every vocabulary column against the Core wire sets
    /// (fail-loud <c>TammaError</c> before the DB CHECK ever fires).
    /// </summary>
    Task<WorkItemEntity> CreateAsync(WorkItemEntity item);

    /// <summary>
    /// Update title/description/kind/priority/type/assignee/estimate/external
    /// ref. Bumps <see cref="WorkItemEntity.Version"/>. Never touches
    /// <c>Key</c>/<c>Number</c>/<c>PreviousKeys</c> (frozen — see
    /// <see cref="RekeyAsync"/>), <c>Status</c> (<see cref="SetStatusAsync"/>),
    /// or the rank columns (<see cref="SetRanksAsync"/>).
    /// </summary>
    Task<WorkItemEntity?> UpdateAsync(WorkItemEntity item);

    /// <summary>
    /// Transition status (wire string, validated). Stamps
    /// <see cref="WorkItemEntity.ClosedAt"/> when the new status is terminal
    /// (derived via <c>WorkItemStatus.IsTerminal()</c> — never a set literal)
    /// and clears it on reopen.
    /// </summary>
    Task<WorkItemEntity?> SetStatusAsync(Guid id, string statusWire);

    /// <summary>
    /// Move the item in either ordering axis. A drag is ONE UPDATE (44-0 D7).
    /// Null leaves that axis unchanged; ranks are validated canonical
    /// (<c>Rank.IsValid</c>).
    /// </summary>
    Task<WorkItemEntity?> SetRanksAsync(Guid id, string? rank, string? siblingRank);

    /// <summary>
    /// Reparent (null = make top-level) and place among the new siblings.
    /// Structural validation (cycles, depth, the Epic kind rule) is Story
    /// 44-3's service — this is the storage seam only.
    /// </summary>
    Task<WorkItemEntity?> SetParentAsync(Guid id, Guid? parentId, string? siblingRank = null);

    /// <summary>
    /// The one sanctioned key change (44-0 AC8): a deliberate operator
    /// re-key. Parses + validates <paramref name="newKey"/>, appends the
    /// outgoing key to <c>PreviousKeys</c> via
    /// <c>WorkItemKeyHistory.Record</c> (idempotent, order-preserving), and
    /// swaps the current key. A project MOVE must NOT call this — the key is
    /// frozen on a move.
    /// </summary>
    Task<WorkItemEntity?> RekeyAsync(Guid id, string newKey);

    /// <summary>
    /// Delete an item. Children RESTRICT the parent FK (44-2 maps to 409);
    /// the item's relation edges cascade away.
    /// </summary>
    Task<bool> DeleteAsync(Guid id);

    // ── Relations (44-0 AC14; stored canonical — D8) ──

    /// <summary>
    /// Add a relation edge. Canonicalizes via the shipped
    /// <c>WorkItemRelationKindExtensions.Canonicalize</c> (self-relation
    /// throws <c>TRACKER.SELF_RELATION</c>; symmetric kinds stored
    /// lower-id-first so a mirror insert collides with the original).
    /// Idempotent: re-adding an existing edge returns the stored row.
    /// </summary>
    Task<WorkItemRelation> AddRelationAsync(
        Guid sourceId, Guid targetId, WorkItemRelationKind kind, Guid? createdByUserId = null);

    /// <summary>Remove an edge (canonicalized before lookup). False when absent.</summary>
    Task<bool> RemoveRelationAsync(Guid sourceId, Guid targetId, WorkItemRelationKind kind);

    /// <summary>Every edge touching the item, in either endpoint position.</summary>
    Task<List<WorkItemRelation>> ListRelationsAsync(Guid workItemId);
}
