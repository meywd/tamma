namespace Tamma.Data.Entities;

/// <summary>
/// Story 35-4 — the control-plane mirror of a tenant's Stripe subscription. One
/// non-terminal row per tenant (a partial-unique index over
/// <see cref="TenantId"/> filtered to non-terminal <see cref="Status"/> values
/// enforces "at most one live subscription"; historical <c>canceled</c> /
/// <c>incomplete_expired</c> rows may accumulate). SaaS only — single-user never
/// writes here (<c>NullBillingProvider</c>).
///
/// <para><b>Mirror is the enforcement source of truth; Stripe is the state
/// source of truth.</b> The mirror exists so Story 35-6 can enforce quota
/// without a Stripe round-trip. On every lifecycle transition the <i>state</i>
/// (<see cref="Status"/>/period/<see cref="TrialEnd"/>) is copied from the Stripe
/// object by <c>SubscriptionMirrorUpdater</c>, never inferred from the API
/// request, so the mirror can never claim a state Stripe has not confirmed
/// (AC13).</para>
///
/// <para>CP-resident (control plane): billing is a cross-cutting concern keyed by
/// tenant, and the webhook (Story 35-5) arrives with no tenant context (tenant is
/// resolved from the Stripe customer id).</para>
/// </summary>
public class BillingSubscription
{
    /// <summary>Stable id (server default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant — FK to <c>tenants.Id</c> (cascade on tenant purge).</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Stripe subscription id (<c>sub_...</c>). Null until the
    /// <c>customer.subscription.created</c> webhook (Story 35-5) materializes the
    /// mirror; unique once assigned (partial index skips the null rows).
    /// </summary>
    public string? StripeSubscriptionId { get; set; }

    /// <summary>Current EFFECTIVE plan slug — <c>free</c> | <c>team</c> | <c>enterprise</c>.</summary>
    public string PlanSlug { get; set; } = "free";

    /// <summary>
    /// Text-domain status pinned by a CHECK constraint:
    /// <c>trialing</c> | <c>active</c> | <c>past_due</c> | <c>canceled</c> |
    /// <c>incomplete</c> | <c>incomplete_expired</c> | <c>unpaid</c>. Copied from
    /// the Stripe subscription object (AC13).
    /// </summary>
    public string Status { get; set; } = "active";

    /// <summary>Current billing-period start (from the Stripe subscription item).</summary>
    public DateTime CurrentPeriodStart { get; set; }

    /// <summary>Current billing-period end (from the Stripe subscription item).</summary>
    public DateTime CurrentPeriodEnd { get; set; }

    /// <summary>True when an at-period-end cancellation is scheduled (status stays <c>active</c>).</summary>
    public bool CancelAtPeriodEnd { get; set; }

    /// <summary>Trial end instant when <see cref="Status"/> is <c>trialing</c>; else null.</summary>
    public DateTime? TrialEnd { get; set; }

    /// <summary>Purchased seat count (drives the seat-cap enforcement in Story 35-6). Default 1.</summary>
    public int Seats { get; set; } = 1;

    // ── Pending downgrade (scheduled at period end via a Stripe Subscription Schedule) ──

    /// <summary>Target slug of a pending downgrade; null when none is scheduled.</summary>
    public string? ScheduledPlanSlug { get; set; }

    /// <summary>When the scheduled downgrade takes effect (= <see cref="CurrentPeriodEnd"/> at schedule time).</summary>
    public DateTime? ScheduledEffectiveAt { get; set; }

    /// <summary>Stripe Subscription Schedule id (<c>sub_sched_...</c>) backing the pending downgrade.</summary>
    public string? StripeScheduleId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Navigation to the owning tenant.</summary>
    public Tenant? Tenant { get; set; }
}
