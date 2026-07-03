using Tamma.Core.Enums;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-3 — the canonical read seam over the AUTHORITATIVE billing-mode
/// owner (<c>TenantProviderBilling</c>). Returns the DECLARED per-<c>(tenant,
/// provider)</c> billing posture as the shared <see cref="MetricBillingMode"/>
/// token. Every reader that needs "what mode did the tenant choose for this
/// provider?" resolves through here so the four legacy enums never drift again:
///
/// <list type="bullet">
///   <item><description>Reader A — the pricing-mode resolver
///     (<see cref="ITenantProviderPricingModeResolver"/>) maps this to
///     <see cref="PricingMode"/> for the cost→price engine.</description></item>
///   <item><description>The 35-2 billing-mode tagger reads this DECLARED mode and
///     reconciles it against Story 32-3's RUNTIME credential source.</description></item>
/// </list>
///
/// <para><b>Default is absence.</b> A null tenant (single-user) or a
/// <c>(tenant, provider)</c> with no <c>active</c> owner row resolves to
/// <see cref="MetricBillingMode.PlatformProvided"/> — the current reality. This
/// is the LEGITIMATE default (35-2 AC8), NOT a fail-loud case. Resolution fails
/// loud ONLY when an <c>active</c> row exists but carries an unparseable mode
/// (which the DB CHECK constraint already prevents) — never a silent mistag.</para>
/// </summary>
public interface ITenantProviderBillingResolver
{
    /// <summary>
    /// The declared <see cref="MetricBillingMode"/> for
    /// <paramref name="tenantId"/> on <paramref name="provider"/>. Null tenant or
    /// no active row ⇒ <see cref="MetricBillingMode.PlatformProvided"/>.
    /// </summary>
    Task<MetricBillingMode> ResolveModeAsync(
        Guid? tenantId, string provider, CancellationToken ct = default);
}
