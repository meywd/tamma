using Microsoft.EntityFrameworkCore;
using Tamma.Core.Enums;
using Tamma.Data;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-5 (interim) — resolves the pricing mode from the per-tenant
/// <c>BillingCustomer.BillingMode</c> column (Story 35-1). This is a real,
/// persisted mode (never invented), but it is per-TENANT rather than
/// per-<c>(tenant, provider)</c>; Story 34-3 replaces it with a per-provider
/// <c>TenantProviderBilling</c> read behind the same
/// <see cref="ITenantProviderPricingModeResolver"/> seam.
///
/// <para>A null tenant (single-user mode) or a tenant with no billing-customer
/// row resolves to <see cref="PricingMode.PlatformProvided"/> — BYOK is opt-in
/// and only ever the result of an explicit <c>BillingMode = "Byok"</c> row.</para>
/// </summary>
public sealed class BillingCustomerPricingModeResolver : ITenantProviderPricingModeResolver
{
    private readonly ControlPlaneDbContext _db;

    public BillingCustomerPricingModeResolver(ControlPlaneDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public async Task<PricingMode> ResolveModeAsync(
        Guid? tenantId, string provider, CancellationToken ct = default)
    {
        if (tenantId is not Guid tid)
        {
            return PricingMode.PlatformProvided;
        }

        var billingMode = await _db.BillingCustomers
            .AsNoTracking()
            .Where(c => c.TenantId == tid)
            .Select(c => c.BillingMode)
            .FirstOrDefaultAsync(ct);

        // BillingMode persists the Core.Billing.BillingMode member name
        // ("PlatformProvided" | "Byok"). Only an explicit Byok row flips to BYOK.
        return string.Equals(billingMode, "Byok", StringComparison.OrdinalIgnoreCase)
            ? PricingMode.Byok
            : PricingMode.PlatformProvided;
    }
}
