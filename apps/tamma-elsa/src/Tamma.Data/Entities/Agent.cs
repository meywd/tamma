namespace Tamma.Data.Entities;

/// <summary>
/// Story 32-1 — a first-class, identity-bearing agent definition. Replaces the
/// anonymous, role-keyed <see cref="AgentConfig"/> JSONB blob as the canonical
/// entity the rest of Epic 32 joins on. The <see cref="Id"/> is the immutable
/// join key for all later action/performance metrics, so an agent's history
/// survives config edits.
///
/// <para><b>Control-plane-resident.</b> Both public (cross-tenant) and private
/// (tenant/user-owned) agent definitions live on
/// <see cref="ControlPlaneDbContext"/> because visibility/identity is a CP
/// concern. ALL performance/action data is tenant-scoped and lands in the
/// tenant schema in later Epic 32 stories — no performance column belongs
/// here; this entity is definition-only.</para>
///
/// <para><b>Mode-aware ownership.</b> Per CLAUDE.md "Universal rule for any
/// tenant-aware feature", ownership is answered separately for each mode:
/// <list type="bullet">
///   <item><see cref="AgentVisibility.Public"/> ⇒ both owner columns NULL.</item>
///   <item><see cref="AgentVisibility.Private"/> in SaaS ⇒
///     <see cref="OwnerTenantId"/> set, <see cref="OwnerUserId"/> NULL.</item>
///   <item><see cref="AgentVisibility.Private"/> in single-user ⇒
///     <see cref="OwnerUserId"/> set, <see cref="OwnerTenantId"/> NULL.</item>
/// </list>
/// This invariant is enforced both by the DB <c>ck_agents_visibility_ownership</c>
/// CHECK (structural backstop) and by an entity-level guard in
/// <c>AgentRepository.CreateAsync</c> (fail-fast before the DB).</para>
/// </summary>
public class Agent
{
    public Guid Id { get; set; }

    /// <summary>Stable handle, e.g. <c>"tamma-architect"</c> or <c>"atlas"</c>.</summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The agent's role as an <c>AgentRole</c> wire string (validated via
    /// <c>RolePhaseMap.NormalizeRole</c> / <c>AgentRoleExtensions.Parse</c> on
    /// create). A benchmarking attribute, NOT a primary key — the Agent, not
    /// the role, is the tracked entity.
    /// </summary>
    public string Role { get; set; } = null!;

    /// <summary>Public (system) vs private (tenant/user-owned). See <see cref="AgentVisibility"/>.</summary>
    public AgentVisibility Visibility { get; set; }

    /// <summary>Owner tenant — set iff <see cref="AgentVisibility.Private"/> in SaaS mode.</summary>
    public Guid? OwnerTenantId { get; set; }

    /// <summary>Owner user — set iff <see cref="AgentVisibility.Private"/> in single-user mode.</summary>
    public Guid? OwnerUserId { get; set; }

    public AgentStatus Status { get; set; } = AgentStatus.Active;

    /// <summary>
    /// Pointer to the active <see cref="AgentVersion"/>. A bare nullable Guid
    /// (no DB FK back to <c>agent_versions</c>) to dodge a circular FK on the
    /// create-then-publish-first-version flow; pointer integrity is enforced in
    /// the repository transaction.
    /// </summary>
    public Guid? CurrentVersionId { get; set; }

    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    /// <summary>Immutable, monotonically-versioned config snapshots for this agent.</summary>
    public ICollection<AgentVersion> Versions { get; set; } = new List<AgentVersion>();
}
