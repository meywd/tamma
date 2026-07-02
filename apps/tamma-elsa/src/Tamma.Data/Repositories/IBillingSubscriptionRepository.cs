using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 35-4 — tenant-scoped CRUD over the <see cref="BillingSubscription"/>
/// mirror. Every method is filtered by <c>TenantId</c>; there is no cross-tenant
/// read path (tenant isolation is structural — AC12).
/// </summary>
public interface IBillingSubscriptionRepository
{
    /// <summary>
    /// The single non-terminal subscription for a tenant, or null when the tenant
    /// has none (free tier) or only terminal (<c>canceled</c>/<c>incomplete_expired</c>)
    /// rows. Tracked so the caller can mutate + save it.
    /// </summary>
    Task<BillingSubscription?> GetActiveByTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Resolve the mirror by its Stripe subscription id (webhook path), or null.</summary>
    Task<BillingSubscription?> GetByStripeSubscriptionIdAsync(
        string stripeSubscriptionId, CancellationToken ct = default);

    /// <summary>Add a new mirror row (not yet saved).</summary>
    Task AddAsync(BillingSubscription subscription, CancellationToken ct = default);
}
