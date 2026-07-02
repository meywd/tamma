using Tamma.Core.Enums;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-5 — the result of pricing a single <see cref="UsageLine"/>. All USD
/// amounts carry 6-decimal internal precision (banker's rounding at each
/// accumulation boundary); invoice-facing projections round to 2dp via
/// <see cref="InvoiceUsd"/>.
/// </summary>
/// <param name="CostBasisUsd">The provider cost (always computed, even for BYOK — for reporting/analytics).</param>
/// <param name="MarginUsd"><c>SellPriceUsd - CostBasisUsd</c> (0 for the BYOK token component).</param>
/// <param name="SellPriceUsd">What the tenant is charged for tokens (0 for BYOK).</param>
/// <param name="PricingMode">The mode this line was priced under.</param>
public sealed record PricedUsage(
    decimal CostBasisUsd,
    decimal MarginUsd,
    decimal SellPriceUsd,
    PricingMode PricingMode)
{
    /// <summary>Round a 6dp internal amount to the 2dp invoice-facing value (banker's rounding).</summary>
    public static decimal InvoiceUsd(decimal amount) => Math.Round(amount, 2, MidpointRounding.ToEven);
}
