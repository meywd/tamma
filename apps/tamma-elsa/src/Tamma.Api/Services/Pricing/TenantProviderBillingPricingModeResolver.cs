using Tamma.Core.Enums;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-3 — the per-<c>(tenant, provider)</c> implementation of
/// <see cref="ITenantProviderPricingModeResolver"/> that Story 34-5 anticipated.
/// It is the "one-line swap behind the seam": it reads the AUTHORITATIVE
/// <c>TenantProviderBilling</c> owner (via
/// <see cref="ITenantProviderBillingResolver"/>) instead of the interim
/// per-TENANT <c>BillingCustomer.BillingMode</c> column, then maps the shared
/// <see cref="MetricBillingMode"/> token to the engine's <see cref="PricingMode"/>
/// vocabulary member-for-member.
///
/// <para>The pricing engine + the estimate endpoint keep consuming
/// <see cref="ITenantProviderPricingModeResolver"/> unchanged — only the wiring
/// behind the seam moved from per-tenant to per-<c>(tenant, provider)</c>.</para>
/// </summary>
public sealed class TenantProviderBillingPricingModeResolver : ITenantProviderPricingModeResolver
{
    private readonly ITenantProviderBillingResolver _owner;

    public TenantProviderBillingPricingModeResolver(ITenantProviderBillingResolver owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    /// <inheritdoc />
    public async Task<PricingMode> ResolveModeAsync(
        Guid? tenantId, string provider, CancellationToken ct = default)
    {
        var mode = await _owner.ResolveModeAsync(tenantId, provider, ct).ConfigureAwait(false);
        return mode == MetricBillingMode.Byok
            ? PricingMode.Byok
            : PricingMode.PlatformProvided;
    }
}
