namespace Tamma.Api.Services.Providers;

/// <summary>
/// Per-(provider, model) USD pricing lookup used by
/// <see cref="HttpProviderClient"/> and <see cref="ProviderSessionService"/>
/// to compute the dollar cost of an invocation from a token-usage tuple.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>packages/cost-monitor/src/{cost-calculator,pricing-config}.ts</c>
/// (commit <c>9e9a57c~1</c>). The TS pricing table is the source of truth for
/// USD-per-1M-token rates. Unknown <c>(provider, model)</c> tuples return
/// zero — the same behaviour as the TS <c>CostCalculator.calculate</c> happy
/// path for local / un-priced models.
/// </para>
/// <para>
/// All rates are quoted as USD per token (i.e. the TS
/// <c>inputPer1MTokens</c> divided by 1,000,000). Computations clamp negative
/// token counts to zero — no upstream protocol legitimately reports negative
/// usage but we don't want a malformed response to flip the cost negative.
/// </para>
/// </remarks>
public interface IProviderPricingService
{
    /// <summary>
    /// Compute the USD cost for a single invocation. Unknown
    /// <c>(provider, model)</c> tuples return <c>0m</c> rather than throwing
    /// so the diagnostic write path stays robust against future model
    /// additions.
    /// </summary>
    decimal Compute(string provider, string? model, int inputTokens, int outputTokens);

    /// <summary>True if a pricing entry exists for the given pair.</summary>
    bool IsKnown(string provider, string? model);
}
