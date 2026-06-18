namespace Tamma.Data.Entities;

/// <summary>
/// Subscription plan referenced by <see cref="Tenant.PlanId"/>. Seeded with
/// three slugs by <c>PlansSeeder</c>: <c>free</c>, <c>team</c>,
/// <c>enterprise</c>. Lives in the control plane.
///
/// <para>Doc 01 §4.1 — the Tenant row references a plan via <c>PlanId</c>;
/// pricing, quotas, and billing cycles are derived from this row at the
/// service layer.</para>
///
/// <para>Story 34-1 — plans are <b>immutable, versioned</b> rows. Editing a
/// plan never mutates an <c>active</c>/<c>deprecated</c> row in place; instead
/// <c>PlanVersionEditor</c> inserts a new <see cref="Version"/> (with
/// <see cref="SupersedesPlanId"/> pointing at the prior) and flips the prior to
/// <c>deprecated</c>, emitting <c>PLAN.VERSION.CREATED</c> /
/// <c>PLAN.DEPRECATED</c> to <c>platform_events</c>. A tenant assigned a
/// specific version keeps that version's pricing/quotas forever. The legacy
/// PATCH <c>/plan</c> path still emits <c>PLAN.UPDATED</c> for the assignment
/// itself. Canonical pricing/quotas now live on the typed child rows
/// (<see cref="Features"/> / <see cref="Entitlements"/> / <see cref="Prices"/>);
/// <see cref="Quotas"/> + <see cref="MonthlyPriceUsd"/> are kept for one
/// deprecation window so not-yet-migrated readers compile.</para>
/// </summary>
public class Plan
{
    /// <summary>Stable UUIDv7 baked into the seed so FK targets stay deterministic across environments.</summary>
    public Guid Id { get; set; }

    /// <summary>Human-friendly slug — <c>free</c>, <c>team</c>, <c>enterprise</c>.</summary>
    public string Slug { get; set; } = null!;

    /// <summary>Display name shown in the billing UI.</summary>
    public string DisplayName { get; set; } = null!;

    /// <summary>
    /// Story 34-1 — monotonic version per <see cref="Slug"/>. A new version
    /// supersedes rather than mutates the prior. <c>UNIQUE (Slug, Version)</c>.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Story 34-1 — lifecycle: <c>draft</c> (mutable, not yet live) |
    /// <c>active</c> (immutable, the current version assignable to tenants) |
    /// <c>deprecated</c> (immutable, superseded but still referenced by
    /// existing tenants). A partial unique index enforces exactly one
    /// <c>active</c> row per slug.
    /// </summary>
    public string Status { get; set; } = "draft";

    /// <summary>Story 34-1 — bespoke (per-customer) enterprise plan flag. Still a platform-owned row.</summary>
    public bool IsCustom { get; set; }

    /// <summary>Story 34-1 — billing cadence: <c>monthly</c> | <c>annual</c>.</summary>
    public string BillingInterval { get; set; } = "monthly";

    /// <summary>Story 34-1 — id of the prior version this row supersedes (NULL for v1).</summary>
    public Guid? SupersedesPlanId { get; set; }

    /// <summary>
    /// Monthly cost in USD. Kept for one deprecation window (display
    /// convenience); the canonical price now lives in <see cref="Prices"/>.
    /// </summary>
    public decimal MonthlyPriceUsd { get; set; }

    /// <summary>
    /// Per-tenant quotas — opaque JSON. Legacy column kept for one deprecation
    /// window; new code reads <see cref="Entitlements"/>.
    /// </summary>
    public string Quotas { get; set; } = "{}";

    /// <summary>True for plans operators are allowed to surface in the
    /// signup UI. Legacy/grandfathered plans flip this off.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Unified-tenancy placement (plan 2026-06-09 §2.3, decision 2):
    /// <c>shared</c> = tenant schema lands in a shared-pool DB;
    /// <c>dedicated</c> = tenant gets a single-tenant DB.
    /// </summary>
    public string PlacementPolicy { get; set; } = "shared";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Story 34-1 — typed feature flags for this plan version.</summary>
    public ICollection<PlanFeature> Features { get; set; } = new List<PlanFeature>();

    /// <summary>Story 34-1 — typed quota entitlements for this plan version.</summary>
    public ICollection<PlanEntitlement> Entitlements { get; set; } = new List<PlanEntitlement>();

    /// <summary>Story 34-1 — pricing rows (one per <c>PricingMode</c>) for this plan version.</summary>
    public ICollection<PlanPrice> Prices { get; set; } = new List<PlanPrice>();
}
