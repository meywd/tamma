namespace Tamma.Data.Entities;

/// <summary>
/// Story 35-1 — the billing catalog row mapping a <c>Plan.Slug</c>
/// (<c>free</c> | <c>team</c> | <c>enterprise</c>) to its Stripe Product, base
/// Price, and the three metered prices + their backing Billing Meters. One row
/// per slug (unique <see cref="PlanSlug"/>); platform-global (never
/// tenant-scoped).
///
/// <para>Deliberately a separate table from <see cref="PlanPrice"/> (Story
/// 34-1): <c>PlanPrice</c> stores tenancy/pricing-policy data keyed by
/// <c>(PlanId, PricingMode)</c>; this row stores the external-Stripe-id binding
/// keyed by slug. Overloading <see cref="Plan"/> columns would couple tenancy
/// placement to Stripe.</para>
///
/// <para>Populated idempotently by the <c>seed-billing</c> CLI command, which
/// upserts the Stripe objects (deterministic idempotency keys) and writes their
/// ids here. Re-running the seed is a no-op: existing ids are reused.</para>
/// </summary>
public class BillingPlanPrice
{
    /// <summary>Stable id (server default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>Plan slug this catalog row maps (unique). <c>free</c> | <c>team</c> | <c>enterprise</c>.</summary>
    public string PlanSlug { get; set; } = null!;

    /// <summary>Stripe Product id (<c>prod_...</c> / our deterministic <c>tamma-plan-{slug}</c>).</summary>
    public string? StripeProductId { get; set; }

    /// <summary>Base flat (per-seat / platform) recurring Price id (<c>price_...</c>).</summary>
    public string? StripePriceId { get; set; }

    /// <summary>Billing Meter id for input-token usage (SUM aggregation).</summary>
    public string? TokensInputMeterId { get; set; }

    /// <summary>Metered Price id charging against the input-token meter.</summary>
    public string? TokensInputPriceId { get; set; }

    /// <summary>Billing Meter id for output-token usage (SUM aggregation).</summary>
    public string? TokensOutputMeterId { get; set; }

    /// <summary>Metered Price id charging against the output-token meter.</summary>
    public string? TokensOutputPriceId { get; set; }

    /// <summary>Billing Meter id for seat usage (LAST / gauge aggregation).</summary>
    public string? SeatsMeterId { get; set; }

    /// <summary>Metered Price id charging against the seats meter.</summary>
    public string? SeatsPriceId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
