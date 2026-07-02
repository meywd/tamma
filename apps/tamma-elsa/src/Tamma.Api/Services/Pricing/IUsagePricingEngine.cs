using Tamma.Data.Entities;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-5 — the CANONICAL cost->price markup engine. Given a measured
/// <see cref="UsageLine"/> and the resolved <see cref="MarginPolicy"/>, computes
/// the billable sell price by applying the margin on top of the
/// <c>IProviderPricingService</c> cost basis (34-11). Consumers — Billing (Epic
/// 35), cost analytics (Epic 36-7), and the usage-event producer (Epic 32-9) —
/// MUST call this engine rather than re-deriving markup.
///
/// <para><b>Pure / side-effect-free:</b> <see cref="PriceUsage"/> does no I/O and
/// is deterministic given the same inputs and the same cost table, which is what
/// makes the golden-file reproducibility test possible. The DB read for the
/// applicable policy lives in <see cref="IMarginPolicyResolver"/>, kept
/// separate.</para>
/// </summary>
public interface IUsagePricingEngine
{
    /// <summary>
    /// Price one usage line under the given margin policy. Throws
    /// <c>PRICING.UNKNOWN_MODEL</c> (a typed <see cref="Tamma.Core.TammaError"/>,
    /// severity Medium) if the <c>(provider, model)</c> pair is unpriced — the
    /// engine never silently prices an unknown model at <c>0</c>.
    /// </summary>
    PricedUsage PriceUsage(UsageLine line, MarginPolicy policy);
}
