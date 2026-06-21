namespace Tamma.Api.Services.Providers;

/// <summary>
/// Story 34-11 (C1 fix) — a cost-cache invalidation seam shared by EVERY
/// component that holds a short-lived in-memory snapshot of the
/// <c>provider_model_prices</c> table. Both the live
/// <see cref="DbProviderPricingService"/> (which backs the unchanged
/// <see cref="IProviderPricingService"/> <c>Compute</c>/<c>IsKnown</c> seam) and
/// the EffectiveFrom-windowed <see cref="IProviderCostResolver"/> hold their OWN
/// snapshot. An admin write that changes pricing/eligibility must clear ALL of
/// them or a live <c>Compute</c> keeps returning the OLD rate for up to the TTL.
///
/// <para>The admin endpoints resolve <c>IEnumerable&lt;IProviderCostCacheInvalidator&gt;</c>
/// and invalidate every registered snapshot in one shot, so there is no stale
/// window after a re-price / register / status-change.</para>
/// </summary>
public interface IProviderCostCacheInvalidator
{
    /// <summary>Clear any cached snapshot of active cost rows (called on an admin write).</summary>
    void Invalidate();
}
