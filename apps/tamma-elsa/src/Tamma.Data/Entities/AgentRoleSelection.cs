namespace Tamma.Data.Entities;

/// <summary>
/// Story 32-2 — which <see cref="Agent"/> serves a given role for a principal.
/// One row per <c>(principal, role)</c>. The <see cref="AgentResolverService"/>
/// entity-aware resolution chain reads this first (precedence branches 1+2);
/// absent a selection it falls to the system-default public agent for the role
/// (branch 3), then fails loud (branch 4) — never an empty/plain config.
///
/// <para><b>Dual-resident, mode-keyed (mirrors <c>prompt_overrides</c>).</b> Per
/// CLAUDE.md "Universal rule for any tenant-aware feature" the principal is
/// answered separately per mode:
/// <list type="bullet">
///   <item>SaaS ⇒ <see cref="TenantId"/> set, <see cref="UserId"/> NULL; the row
///     lives in the tenant's <c>t_&lt;hex&gt;</c> schema
///     (<c>TenantDbContext</c>).</item>
///   <item>single-user ⇒ <see cref="UserId"/> set, <see cref="TenantId"/> NULL;
///     the row lives on the control plane (<c>ControlPlaneDbContext</c>).</item>
/// </list>
/// Exactly one of the two is non-null — enforced by the
/// <c>ck_agent_role_selections_principal_xor</c> CHECK (mirrors
/// <c>ck_prompt_overrides_principal_xor</c>) and the
/// <c>UNIQUE NULLS NOT DISTINCT (TenantId, UserId, Role)</c> index.</para>
///
/// <para><b>Cross-schema reference, not a DB FK.</b> The selected
/// <see cref="Agent"/> is CP-resident (both public and own-private), so there is
/// no DB FK from this row to <c>agents</c>. The registry validates the target is
/// in (public ∪ own private) at write time, and the resolver RE-validates at
/// resolve time so an archived/deleted target degrades to the system default
/// rather than resolving stale. The stored <see cref="Visibility"/> is a hint
/// only — provenance is recomputed, never trusted.</para>
/// </summary>
public class AgentRoleSelection
{
    public Guid Id { get; set; }

    /// <summary>SaaS principal — set iff <see cref="UserId"/> is NULL.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>single-user principal — set iff <see cref="TenantId"/> is NULL.</summary>
    public Guid? UserId { get; set; }

    /// <summary>One of <c>RolePhaseMap.ValidRoles</c> (canonical wire string).</summary>
    public string Role { get; set; } = null!;

    /// <summary>
    /// The selected agent (public OR own private). Logical reference only — the
    /// target is CP-resident; cross-schema FKs are not modelled.
    /// </summary>
    public Guid AgentId { get; set; }

    /// <summary>
    /// Provenance hint captured at selection time: <c>tenant-private</c> |
    /// <c>tenant-public</c> | <c>system-public</c>. Recomputed on resolve, never
    /// trusted.
    /// </summary>
    public string Visibility { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>User id that performed the most recent upsert (audit).</summary>
    public Guid? UpdatedBy { get; set; }
}
