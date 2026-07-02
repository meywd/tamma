using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Providers;
using Tamma.Core;
using Tamma.Core.Enums;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-5 — the pure, deterministic implementation of
/// <see cref="IUsagePricingEngine"/>. Applies the margin policy on top of the
/// <see cref="IProviderPricingService"/> cost basis (34-11).
///
/// <para><b>Arithmetic (AC5 / AC7):</b> <c>decimal</c> throughout with banker's
/// rounding (<see cref="MidpointRounding.ToEven"/>) to 6 decimal places at every
/// accumulation boundary, so the output is byte-stable across runs. Platform:
/// <c>sell = round6(costBasis * (multiplier ?? 1) + (fixedUsdPer1M ?? 0) *
/// totalTokens/1e6)</c>, <c>margin = round6(sell - costBasis)</c>. BYOK: the
/// token component of the sell price and the margin are exactly <c>0</c>, but the
/// cost basis is STILL computed (for reporting/analytics).</para>
///
/// <para><b>Fail-loud (AC8):</b> <see cref="IProviderPricingService.Compute"/>
/// returns <c>0m</c> for an unknown <c>(provider, model)</c>, so the engine gates
/// on <see cref="IProviderPricingService.IsKnown"/> FIRST and throws
/// <c>PRICING.UNKNOWN_MODEL</c> — a misconfigured model is loud, never free.</para>
/// </summary>
public sealed class UsagePricingEngine : IUsagePricingEngine
{
    private readonly IProviderPricingService _pricing;
    private readonly ILogger<UsagePricingEngine> _logger;

    public UsagePricingEngine(
        IProviderPricingService pricing,
        ILogger<UsagePricingEngine> logger)
    {
        _pricing = pricing ?? throw new ArgumentNullException(nameof(pricing));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public PricedUsage PriceUsage(UsageLine line, MarginPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(policy);

        // AC8 — gate on IsKnown so an unpriced model is loud, not silently $0.
        if (!_pricing.IsKnown(line.Provider, line.Model))
        {
            _logger.LogWarning(
                "Unknown (provider, model) for pricing: {Provider}/{Model} — refusing to price at 0",
                line.Provider, line.Model ?? "");

            throw new TammaError(
                "PRICING.UNKNOWN_MODEL",
                $"No cost pricing exists for {line.Provider}/{line.Model ?? "(default)"}.",
                new Dictionary<string, object?>
                {
                    ["provider"] = line.Provider,
                    ["model"] = line.Model ?? "",
                },
                retryable: false,
                severity: TammaErrorSeverity.Medium);
        }

        var costBasis = Round6(_pricing.Compute(
            line.Provider, line.Model, line.InputTokens, line.OutputTokens));

        // BYOK — the tenant pays their provider directly; Tamma bills no token
        // price. Cost basis is still reported (analytics), margin is 0.
        if (line.PricingMode == PricingMode.Byok)
        {
            return new PricedUsage(costBasis, 0m, 0m, PricingMode.Byok);
        }

        // Platform-provided — apply the margin policy.
        var totalTokens = (long)Math.Max(0, line.InputTokens) + Math.Max(0, line.OutputTokens);
        var multiplied = Round6(costBasis * (policy.MarkupMultiplier ?? 1m));
        var fixedAdd = Round6((policy.FixedUsdPer1M ?? 0m) * (totalTokens / 1_000_000m));
        var sell = Round6(multiplied + fixedAdd);
        var margin = Round6(sell - costBasis);

        _logger.LogDebug(
            "Priced {Provider}/{Model}: costBasisUsd={CostBasis} sellPriceUsd={Sell} (scope={Scope}, refKey={RefKey})",
            line.Provider, line.Model ?? "", costBasis, sell, policy.Scope, policy.RefKey ?? "");

        return new PricedUsage(costBasis, margin, sell, PricingMode.PlatformProvided);
    }

    /// <summary>Banker's rounding to 6dp — the internal precision boundary (AC7).</summary>
    private static decimal Round6(decimal v) => Math.Round(v, 6, MidpointRounding.ToEven);
}
