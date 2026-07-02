namespace Tamma.Data.Entities;

/// <summary>
/// Story 34-4 — audited, version-pinned record of which plan a tenant is on.
/// Replaces the loose <see cref="Tenant.Plan"/> string + the Epic-28
/// <c>Tenant.PlanId</c> shadow column as the SOURCE OF TRUTH for "what plan is
/// this tenant on right now (and which version)". At most one row per tenant has
/// <c>Status = 'active'</c> (a partial unique index enforces it).
///
/// <para><see cref="PlanVersion"/> is a DENORMALIZED, pinned copy of
/// <c>Plan.Version</c> (34-1) — never a join to the current plan row — so a
/// later plan deprecation (34-1/34-2 flips the active version and mints N+1)
/// can NEVER retro-price an existing tenant. Reading a tenant's effective plan
/// resolves the pinned <c>(PlanId, PlanVersion)</c> snapshot, never "latest".</para>
///
/// <para>CP-resident (lives on <c>ControlPlaneDbContext</c>): plan assignment is
/// a control-plane concern keyed by tenant, alongside the <c>plans</c> catalog
/// and the tenant registry.</para>
/// </summary>
public class TenantPlanAssignment
{
    /// <summary>UUIDv7 (DB default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>FK → <c>tenants.Id</c>. Cascade-deleted with the tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>FK → <c>plans.Id</c> — a specific VERSIONED plan row (34-1). Restrict-deleted.</summary>
    public Guid PlanId { get; set; }

    /// <summary>Pinned copy of <c>Plan.Version</c> at assignment time (never re-read).</summary>
    public int PlanVersion { get; set; }

    /// <summary>
    /// <c>active</c> | <c>scheduled</c> | <c>cancelled</c>
    /// (<see cref="Tamma.Core.Enums.PlanAssignmentStatus"/>).
    /// </summary>
    public string Status { get; set; } = "active";

    /// <summary>UTC instant this assignment became (or is scheduled to become) effective.</summary>
    public DateTime EffectiveFrom { get; set; }

    /// <summary>
    /// UTC instant this assignment stopped being active (the proration boundary),
    /// or <c>null</c> while it is still <c>active</c>/<c>scheduled</c>.
    /// </summary>
    public DateTime? EffectiveTo { get; set; }

    /// <summary>The actor who made the change; <c>null</c> = system / scheduler.</summary>
    public Guid? AssignedByUserId { get; set; }

    /// <summary>Free-text reason (admin note / self-service / backfill), nullable.</summary>
    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
