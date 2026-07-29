namespace Tamma.Data.Entities;

/// <summary>
/// A tracker iteration (Story 44-1 — table <c>iterations</c>, tenant-schema
/// resident). Orthogonal to the work-item hierarchy: an FK on the work item
/// (<c>WorkItemEntity.IterationId</c>), not a level (epic README §3). Created
/// by this story so all tracker tables land in ONE tenant migration; only
/// populated by Story 44-4 (iterations, board projection, SprintPlan apply
/// seam) — inert until then.
/// </summary>
public class IterationEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public DateOnly? StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }

    /// <summary><c>planned</c> | <c>active</c> | <c>closed</c> (CHECK-enforced; 44-4 owns transitions).</summary>
    public string Status { get; set; } = "planned";

    /// <summary>Optional capacity in the project's estimate scale units.</summary>
    public decimal? CapacityPoints { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Optimistic-concurrency counter, bumped on every write.</summary>
    public int Version { get; set; } = 1;
}
