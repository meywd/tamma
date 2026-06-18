using Tamma.Data.Entities;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-1 — read side of the billing catalog: resolve the
/// <see cref="BillingPlanPrice"/> (Stripe Product/Price/Meter ids) for a plan
/// slug. Backs the downstream Epic 35 stories (subscriptions, metering) that
/// need the slug→Stripe-ids binding. Platform-global; never tenant-scoped.
/// </summary>
public interface IBillingCatalog
{
    /// <summary>
    /// Resolve the catalog row for a slug. Throws
    /// <c>BILLING.CATALOG.UNKNOWN_SLUG</c> when no row exists (the seed has not
    /// run, or the slug is invalid) — never a silent null.
    /// </summary>
    Task<BillingPlanPrice> GetBySlugAsync(string planSlug, CancellationToken ct = default);

    /// <summary>Resolve the catalog row for a slug, or null when absent.</summary>
    Task<BillingPlanPrice?> TryGetBySlugAsync(string planSlug, CancellationToken ct = default);
}
