namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-4 — the subscription lifecycle seam. Orchestrates Stripe (via the
/// 35-1 client factory) + the local mirror (<see cref="SubscriptionMirrorUpdater"/>)
/// + DCB events for checkout / plan change / cancel / seats / read. SaaS only —
/// in single-user the 35-1 <c>NullBillingProvider</c> (<c>IsEnabled = false</c>)
/// makes every mutating call a hard SaaS-only error and zero Stripe calls (AC11).
/// </summary>
public interface ISubscriptionService
{
    /// <summary>
    /// AC2 — build a Stripe Checkout Session (<c>mode=subscription</c>) for the
    /// slug (+ optional seats/trial). NO local row is created — the mirror is
    /// materialized by the Story 35-5 <c>customer.subscription.created</c> webhook.
    /// </summary>
    Task<CheckoutResult> CreateCheckoutSessionAsync(
        Guid tenantId, string planSlug, int? seats, int? trialDays, CancellationToken ct = default);

    /// <summary>
    /// AC3 — upgrade (immediate proration) when the target plan's monthly price ≥
    /// the current plan's; schedule a downgrade at period end otherwise.
    /// </summary>
    Task<SubscriptionProjection> ChangePlanAsync(
        Guid tenantId, string newPlanSlug, CancellationToken ct = default);

    /// <summary>AC4 — cancel immediately (status→canceled, plan→free now) or at period end.</summary>
    Task<SubscriptionProjection> CancelAsync(
        Guid tenantId, bool atPeriodEnd, CancellationToken ct = default);

    /// <summary>
    /// AC6 — change the seat quantity. Rejected with a <c>seats_below_active_members</c>
    /// conflict (→ 409) when below the tenant's active-membership count, before any
    /// Stripe call.
    /// </summary>
    Task<SubscriptionProjection> ChangeSeatsAsync(
        Guid tenantId, int seats, CancellationToken ct = default);

    /// <summary>AC9 — the current subscription projection; free-tier default when none.</summary>
    Task<SubscriptionProjection> GetAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>The checkout-session result returned to the tenant owner/admin (AC2).</summary>
public sealed record CheckoutResult(string CheckoutUrl, string StripeSessionId);
