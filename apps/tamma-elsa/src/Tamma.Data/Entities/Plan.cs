namespace Tamma.Data.Entities;

/// <summary>
/// Subscription plan referenced by <see cref="Tenant.PlanId"/>. Seeded with
/// three rows by <c>PlansSeeder</c>: <c>plan_free</c>, <c>plan_team</c>,
/// <c>plan_enterprise</c>. Lives in the control plane.
///
/// <para>Doc 01 §4.1 — the Tenant row references a plan via <c>PlanId</c>;
/// pricing, quotas, and billing cycles are derived from this row at the
/// service layer. Plans are immutable in the seed; runtime changes are an
/// admin-only operation that emits <c>PLAN.UPDATED</c> to
/// <c>platform_events</c>.</para>
/// </summary>
public class Plan
{
    /// <summary>Stable UUIDv7 baked into the seed so FK targets stay deterministic across environments.</summary>
    public Guid Id { get; set; }

    /// <summary>Human-friendly slug — <c>free</c>, <c>team</c>, <c>enterprise</c>.</summary>
    public string Slug { get; set; } = null!;

    /// <summary>Display name shown in the billing UI.</summary>
    public string DisplayName { get; set; } = null!;

    /// <summary>Monthly cost in USD. Zero for the free tier.</summary>
    public decimal MonthlyPriceUsd { get; set; }

    /// <summary>
    /// Per-tenant quotas (LLM tokens/month, concurrent workflows, seat
    /// caps, etc.) — opaque JSON consumed by the billing layer.
    /// </summary>
    public string Quotas { get; set; } = "{}";

    /// <summary>True for plans operators are allowed to surface in the
    /// signup UI. Legacy/grandfathered plans flip this off.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
