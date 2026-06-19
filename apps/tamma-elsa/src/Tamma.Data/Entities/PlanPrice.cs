namespace Tamma.Data.Entities;

/// <summary>
/// Story 34-1 — recurring + per-seat + metered pricing for a <see cref="Plan"/>
/// version, split by <see cref="PricingMode"/> so BYOK tenants
/// (bring-your-own AI keys) get a distinct price row from platform-provided
/// tenants under the same plan version. One row per <c>(PlanId, PricingMode)</c>.
///
/// <para>This story stores pricing verbatim. It does NOT charge (Epic 35 owns
/// Stripe), it does NOT compute cost→price markup (a separate Epic 34 story
/// reads <see cref="MeteredComponent"/>), and it does NOT resolve which mode
/// applies to a tenant (Story 34-3) — both rows are stored; the consumer
/// picks.</para>
/// </summary>
public class PlanPrice
{
    /// <summary>Stable id (server default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>Owning plan version.</summary>
    public Guid PlanId { get; set; }

    /// <summary>Pricing mode: <c>platform_provided</c> | <c>byok</c>.</summary>
    public string PricingMode { get; set; } = "platform_provided";

    /// <summary>Flat recurring (per billing interval) charge in USD.</summary>
    public decimal RecurringUsd { get; set; }

    /// <summary>Per-seat charge in USD.</summary>
    public decimal SeatUsd { get; set; }

    /// <summary>
    /// Metered / overage pricing components — opaque jsonb stored verbatim
    /// (e.g. per-1k-token rates keyed by <c>EntitlementMetricKey</c>). The
    /// markup engine (separate story) and billing (Epic 35) interpret it;
    /// this story never computes it. Defaults to <c>'{}'</c>.
    /// </summary>
    public string MeteredComponent { get; set; } = "{}";
}
