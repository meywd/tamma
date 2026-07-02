using Tamma.Core.Enums;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-5 — the consumption seam for the per-<c>(tenant, provider)</c>
/// pricing mode (BYOK vs platform-provided). The engine and the estimate
/// endpoint READ the mode through this seam; they never invent it (AC12).
///
/// <para><b>Ownership / interim note:</b> the authoritative source is Story
/// 34-3's <c>TenantProviderBilling</c> / <c>ProviderDiagnostic.BillingMode</c>,
/// which is per-<c>(tenant, provider)</c>. Until 34-3 lands, the default
/// implementation (<see cref="BillingCustomerPricingModeResolver"/>) reads the
/// per-tenant <c>BillingCustomer.BillingMode</c> (Story 35-1) — the best signal
/// available today — and 34-3 swaps a per-provider implementation behind this
/// same interface with no engine change.</para>
/// </summary>
public interface ITenantProviderPricingModeResolver
{
    /// <summary>
    /// The pricing mode for <paramref name="tenantId"/> on
    /// <paramref name="provider"/>. A null tenant (single-user mode) or an
    /// unknown tenant resolves to <see cref="PricingMode.PlatformProvided"/> —
    /// the safe default (a BYOK tenant is opt-in and explicitly recorded).
    /// </summary>
    Task<PricingMode> ResolveModeAsync(
        Guid? tenantId, string provider, CancellationToken ct = default);
}
