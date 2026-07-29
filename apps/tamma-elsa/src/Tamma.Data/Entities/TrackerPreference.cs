namespace Tamma.Data.Entities;

/// <summary>
/// A stored tracker-preference row (Story 44-1 AC6 — table
/// <c>tracker_preferences</c>), mirroring <see cref="AcceptanceRulesOverride"/>'s
/// dual-scoping shape exactly: single-user rows are keyed on <see cref="UserId"/>
/// (tenant_id NULL), SaaS rows on <see cref="TenantId"/> (user_id NULL); the
/// STRONG <c>principal_xor</c> CHECK enforces exactly-one (both-NULL rejected —
/// the <c>acceptance_rules_overrides</c> form, not the weak <c>audit_records</c>
/// one), and the unique <c>(UserId, TenantId)</c> index carries
/// <c>NULLS NOT DISTINCT</c> so both planes dedupe on their null half.
///
/// <para>This is the ONE tracker table where the principal pattern applies:
/// default project / default kind / board grouping are genuine per-principal
/// configuration. Work items, projects, relations and iterations are content
/// and carry no principal plane (epic D6).</para>
/// </summary>
public class TrackerPreference
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public Guid? TenantId { get; set; }

    /// <summary>The principal's default project for creates/board landing.</summary>
    public Guid? DefaultProjectId { get; set; }

    /// <summary><c>WorkItemKind</c> wire string used as the create-form default; NULL = unset.</summary>
    public string? DefaultKind { get; set; }

    /// <summary>Default board grouping (e.g. <c>status</c>); free text, 44-6 owns the UI vocabulary.</summary>
    public string? BoardGroupBy { get; set; }

    /// <summary>User id that originally created the row.</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>User id of the most recent updater.</summary>
    public Guid? UpdatedBy { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Optimistic-concurrency counter, bumped on every upsert.</summary>
    public int Version { get; set; } = 1;
}
