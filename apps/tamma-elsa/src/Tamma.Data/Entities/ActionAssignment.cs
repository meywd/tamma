namespace Tamma.Data.Entities;

/// <summary>
/// Story 43-5 (AC1) — one autonomy assignment for an action, a group, or a
/// mode, at one of THREE scopes: PLATFORM (both principal columns null — the
/// ceiling a tenant cannot go below), TENANT (SaaS) or USER (single-user).
///
/// <para><b>Control-plane resident in BOTH modes — FORCED, not preferred</b>
/// (43-5 D1, all three reasons so nobody reopens it): (i) background actors
/// and <c>PlatformTaskWorker</c> have no ambient tenant context — the shipped
/// tenant-resident posture (<c>AcceptanceRulesRepository.RequireTenantId</c>)
/// throws, so a gate consulted from a sweeper would throw on every tick;
/// (ii) the engine plane may carry no tenant at all
/// (<c>ServiceAuthPrincipal.TenantId</c> is nullable by design); (iii)
/// decisively, a new tenant migration NEVER reaches already-provisioned
/// tenants — <c>ITenantDbMigrator</c> has exactly two creation-only call
/// sites and no startup sweep, so a tenant-resident table would 42P01 on
/// every gate read for every existing tenant.</para>
///
/// <para><b>Deliberately EXCLUDED from the destructive startup DROP list</b>
/// (43-5 AC5/D3): every other table on that list is operational data; these
/// rows are the only thing between an agent and a production deploy, and the
/// list runs on every restart without <c>TAMMA_PRESERVE_DB=1</c>. A safety
/// table on that list would silently revert every admin tightening on the
/// next restart — a governance surface that lies. The exclusion is pinned by
/// <c>ActionGovernanceResidencyTests</c>, which reads the actual
/// <c>ExecuteSqlRaw</c> literal. Consequences (the <c>provider_settings</c>
/// survival pattern): NO FK to <c>tenants</c>/<c>users</c> (those ARE wiped;
/// a cascade would defeat the exclusion) and an IF-NOT-EXISTS idempotent
/// migration (the migration history is rebuilt while this table persists).</para>
///
/// <para><b>The principal CHECK is <c>ck_action_assignments_principal_scope</c>
/// — deliberately NOT <c>_principal_xor</c></b> (43-5 D2): six shipped stores
/// use <c>_principal_xor</c> with exactly two admissible cases; this table
/// admits a THIRD — neither key set — which IS the platform ceiling. The name
/// stops a reader who pattern-matches the XOR stores from "fixing" the
/// ceiling away.</para>
///
/// <para><b>All three policy columns are nullable</b> (AC2/D4): null means
/// "unset — inherit from the next tier". A non-nullable
/// <c>enabled DEFAULT TRUE</c> would make a threshold-only write silently
/// re-enable a group-disabled action. "No opinion at all" is the ABSENCE of
/// the row (DELETE falls back to the next tier). There is deliberately NO DB
/// CHECK on <see cref="MinAutonomy"/> (AC3/D5): a CHECK frozen into a
/// migration snapshot would be a second permanent hardcoding of the dial
/// bound; the single source is <c>AutonomyDial</c>, validated domain-side.</para>
/// </summary>
public class ActionAssignment
{
    public Guid Id { get; set; }

    /// <summary>SaaS principal — set iff <see cref="UserId"/> is NULL. Both
    /// null = the PLATFORM ceiling row.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Single-user principal — set iff <see cref="TenantId"/> is
    /// NULL. Both null = the PLATFORM ceiling row.</summary>
    public Guid? UserId { get; set; }

    /// <summary><c>action</c> | <c>group</c> | <c>mode</c>
    /// (<c>ck_action_assignments_target_kind</c>).</summary>
    public string TargetKind { get; set; } = null!;

    /// <summary>The target's wire string: an <c>ActionKey</c> wire
    /// (<c>"tool:file_write"</c>) for <c>action</c> rows, an
    /// <c>ActionGroup</c> wire (<c>"deploy-control"</c>) for <c>group</c>
    /// rows.</summary>
    public string TargetKey { get; set; } = null!;

    /// <summary>The minimum autonomy threshold — automated iff
    /// <c>dial &gt;= MinAutonomy</c>; <c>AutonomyDial.AlwaysHuman</c> means a
    /// person decides at every level. NULL only on <c>mode</c> rows
    /// (<c>ck_action_assignments_mode_row</c>). NO numeric DB CHECK —
    /// deliberate (AC3/D5).</summary>
    public int? MinAutonomy { get; set; }

    /// <summary>Whether a below-threshold resolution BLOCKS (true) or is
    /// observe-only (false). NULL = inherit (defaults true — epic D1, v1
    /// enforces).</summary>
    public bool? Enforce { get; set; }

    /// <summary>Whether the action may run at all. NULL = inherit (defaults
    /// true). Resolves monotone across scopes: either plane's FALSE wins.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Optional agent-role allowlist (role wire strings). NULL =
    /// inherit / no restriction.</summary>
    public string[]? AllowedRoles { get; set; }

    /// <summary>Optional admin-authored note (why this assignment exists).</summary>
    public string? Note { get; set; }

    /// <summary>Monotonic per-row version (audit; bumped on every upsert).</summary>
    public int Version { get; set; }

    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
