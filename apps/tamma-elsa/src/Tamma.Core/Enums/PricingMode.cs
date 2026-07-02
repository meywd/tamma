namespace Tamma.Core.Enums;

/// <summary>
/// Story 34-5 — the pricing-side vocabulary for how a measured usage line is
/// priced by <c>IUsagePricingEngine</c>. Distinct enum from
/// <see cref="Tamma.Core.Billing.BillingMode"/> (which is the billing-customer
/// column vocabulary, Story 35-1) but carries the SAME two members so the two
/// layers agree on the BYOK-vs-platform split. The engine's public records
/// (<c>UsageLine</c> / <c>PricedUsage</c>) speak "pricing mode"; a caller that
/// only has a <see cref="Tamma.Core.Billing.BillingMode"/> maps member-for-member.
///
/// <para><b>Load-bearing rule (Story 34-5 AC5):</b> on the
/// <see cref="PlatformProvided"/> leg the token sell price is the cost basis
/// times the margin policy; on the <see cref="Byok"/> leg the token sell price
/// (and margin) is exactly <c>0</c> — the tenant pays their own provider
/// directly, Tamma bills no token price. The cost basis is computed on BOTH legs
/// for reporting/analytics.</para>
/// </summary>
public enum PricingMode
{
    /// <summary>
    /// Tamma supplies the provider credential and bills the tenant for metered
    /// token usage with the platform markup applied.
    /// </summary>
    PlatformProvided,

    /// <summary>
    /// Bring-your-own-key — the tenant supplies their own provider credential.
    /// The token component of the sell price is <c>0</c> (no platform markup);
    /// the cost basis is still computed for reporting only.
    /// </summary>
    Byok,
}
