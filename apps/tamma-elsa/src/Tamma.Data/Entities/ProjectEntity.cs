namespace Tamma.Data.Entities;

/// <summary>
/// A tracker project (Story 44-1 — table <c>projects</c>, tenant-schema
/// resident). Owns the key prefix (<see cref="Key"/>, validated by
/// <c>WorkItemRef.IsValidProjectKey</c> at the repository boundary), the
/// per-project work-item number sequence (<see cref="NextNumber"/>, consumed
/// under a <c>FOR UPDATE</c> row lock by <c>WorkItemRepository.CreateAsync</c>),
/// the repository binding and the estimate scale. A project is not a work item
/// and never appears on a board (epic README §3).
///
/// <para>No principal XOR / no <c>TenantId</c> column: work-tracking rows are
/// tenant-schema CONTENT — the schema is the isolation plane (epic D6).</para>
/// </summary>
public class ProjectEntity
{
    public Guid Id { get; set; }

    /// <summary>
    /// The frozen key prefix (e.g. <c>TAM</c>), <c>^[A-Z][A-Z0-9]{1,9}$</c>.
    /// Unique per tenant. Renaming it is an operator re-key that flows through
    /// <c>IWorkItemRepository.RekeyAsync</c> (which records
    /// <c>WorkItemEntity.PreviousKeys</c>); the column itself never cascades
    /// into already-minted work-item keys.
    /// </summary>
    public string Key { get; set; } = null!;

    public string Name { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>
    /// Story 44-1 AC10 — nullable, deliberately NOT a foreign key. Points at
    /// Story 39-20's control-plane <c>repositories</c> row once that lands; a
    /// cross-plane FK is not expressible and Epic 44 must not create a second
    /// repo registry (epic boundary table).
    /// </summary>
    public Guid? RepositoryId { get; set; }

    /// <summary>
    /// Story 44-0 AC13 — the project's estimate scale as an
    /// <c>EstimateScale</c> wire string (<c>not_used</c> default). The work
    /// item stores a scale-free <see cref="WorkItemEntity.Estimate"/>; naming
    /// the scale here (not in the column) is what lets a team change its mind
    /// without a migration.
    /// </summary>
    public string EstimateScale { get; set; } = "not_used";

    /// <summary>
    /// Next work-item number to mint for this project (D6). Read+incremented
    /// under <c>SELECT ... FOR UPDATE</c> inside the create transaction so
    /// concurrent creates cannot collide and numbering is gap-free.
    /// </summary>
    public int NextNumber { get; set; } = 1;

    public DateTime? ArchivedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Optimistic-concurrency counter, bumped on every write (44-2's ETag).</summary>
    public int Version { get; set; } = 1;
}
