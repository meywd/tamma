namespace Tamma.Data.Entities;

/// <summary>
/// Story 32-16 — per-tenant agent/persona ENABLEMENT (Epic 32 locked model rule
/// 6 / design §3.3). Catalog membership: which PUBLIC personas a tenant exposes
/// to its members. The tenant's usable set is
/// <c>enabled(public) ∪ own-private</c>, not every public persona on the platform.
///
/// <para><b>Enablement vs selection.</b> This is <i>catalog membership</i> — which
/// personas are part of this tenant's set. It is distinct from
/// <see cref="AgentRoleSelection"/> (32-2), which is <i>role binding</i> — which
/// (already-enabled) agent serves a given role. Enablement gates selection, never
/// the reverse.</para>
///
/// <para><b>CP-resident in BOTH modes.</b> Unlike <see cref="AgentRoleSelection"/>
/// (tenant-schema in SaaS, CP for single-user), this table is control-plane
/// resident in <i>both</i> modes because it gates the CP-resident public
/// <see cref="Agent"/> catalog and is keyed by tenant id (SaaS) / user id
/// (single-user), not stored per <c>t_&lt;hex&gt;</c>. Hence it joins the
/// <c>Program.cs</c> startup-reset DROP list and the
/// <c>ControlPlaneDbContextModelTests</c> strict entity list.</para>
///
/// <para><b>Principal XOR + dual-keying</b> (mirrors <see cref="AgentRoleSelection"/>
/// / <c>prompt_overrides</c>): exactly one of <see cref="TenantId"/> (SaaS) XOR
/// <see cref="UserId"/> (single-user) is non-null — enforced by the
/// <c>ck_tenant_agent_enablements_principal_xor</c> CHECK and the
/// <c>UNIQUE NULLS NOT DISTINCT (TenantId, UserId, AgentId)</c> index. There is NO
/// per-user enablement layer in SaaS (CLAUDE.md "no per-user override layer").</para>
///
/// <para><b>Default-deny for public personas.</b> Absent a row, a public persona
/// is NOT enabled. Own private/custom agents are implicitly enabled by authorship
/// (no row required). A fresh tenant is seeded with the platform default persona
/// enabled (insert-missing-only) so it is usable out of the box.</para>
/// </summary>
public class TenantAgentEnablement
{
    public Guid Id { get; set; }

    /// <summary>SaaS principal — set iff <see cref="UserId"/> is NULL.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>single-user principal — set iff <see cref="TenantId"/> is NULL.</summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// A public persona OR an own private/custom agent. Logical reference only —
    /// public agents live in the CP catalog; no cross-schema DB FK is modelled.
    /// The service validates the target is in (public ∪ own-private) at write time.
    /// </summary>
    public Guid AgentId { get; set; }

    /// <summary>
    /// True = part of the tenant's usable catalog. A disable sets this false. An
    /// absent row for a public persona = not enabled (default-deny).
    /// </summary>
    public bool Enabled { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>User id that created the row (audit).</summary>
    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>User id that performed the most recent upsert (audit).</summary>
    public Guid? UpdatedBy { get; set; }
}
