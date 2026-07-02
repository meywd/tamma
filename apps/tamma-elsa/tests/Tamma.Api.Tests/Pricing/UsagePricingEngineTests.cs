using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.Providers;
using Tamma.Core;
using Tamma.Core.Enums;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-5 (AC5, AC7, AC8) — the PURE cost->price engine. Cost basis is a
/// controlled input (mocked <see cref="IProviderPricingService"/>); no DB. Covers
/// the platform markup math (multiplier-only / fixed-only / combined), the BYOK
/// zero-token-markup rule, the unknown-model fail-loud, rounding determinism, and
/// a committed golden-file byte-stability regression.
/// </summary>
[TestFixture]
public class UsagePricingEngineTests
{
    private static UsagePricingEngine NewEngine(IProviderPricingService pricing) =>
        new(pricing, NullLogger<UsagePricingEngine>.Instance);

    private static Mock<IProviderPricingService> KnownPricing(decimal costBasis)
    {
        var mock = new Mock<IProviderPricingService>();
        mock.Setup(m => m.IsKnown(It.IsAny<string>(), It.IsAny<string?>())).Returns(true);
        mock.Setup(m => m.Compute(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(costBasis);
        return mock;
    }

    private static MarginPolicy Policy(decimal? multiplier, decimal? fixedPer1M) => new()
    {
        Id = Guid.NewGuid(),
        Scope = "global",
        RefKey = null,
        MarkupMultiplier = multiplier,
        FixedUsdPer1M = fixedPer1M,
        EffectiveFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Status = "active",
    };

    private static UsageLine Line(PricingMode mode, int inTok = 1000, int outTok = 500) =>
        new("anthropic", "claude-sonnet-4-20250514", inTok, outTok, mode,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    [Test]
    public void PriceUsage_PlatformMultiplierOnly_AppliesMarkup()
    {
        var engine = NewEngine(KnownPricing(1.0m).Object);

        var result = engine.PriceUsage(Line(PricingMode.PlatformProvided), Policy(1.3m, null));

        result.CostBasisUsd.Should().Be(1.000000m);
        result.SellPriceUsd.Should().Be(1.300000m);
        result.MarginUsd.Should().Be(0.300000m);
        result.PricingMode.Should().Be(PricingMode.PlatformProvided);
    }

    [Test]
    public void PriceUsage_PlatformFixedPer1MOnly_AddsFixedComponent()
    {
        var engine = NewEngine(KnownPricing(1.0m).Object);

        // 600k + 400k = 1,000,000 total tokens ⇒ fixed adds exactly $0.50.
        var result = engine.PriceUsage(
            Line(PricingMode.PlatformProvided, 600_000, 400_000), Policy(null, 0.50m));

        result.CostBasisUsd.Should().Be(1.000000m);
        result.SellPriceUsd.Should().Be(1.500000m);
        result.MarginUsd.Should().Be(0.500000m);
    }

    [Test]
    public void PriceUsage_PlatformMultiplierPlusFixed_CombinesBoth()
    {
        var engine = NewEngine(KnownPricing(1.0m).Object);

        var result = engine.PriceUsage(
            Line(PricingMode.PlatformProvided, 600_000, 400_000), Policy(1.3m, 0.50m));

        // 1.0 * 1.3 = 1.30 (+ 0.50 fixed) = 1.80.
        result.SellPriceUsd.Should().Be(1.800000m);
        result.MarginUsd.Should().Be(0.800000m);
    }

    [Test]
    public void PriceUsage_Byok_ZeroTokenSellPrice_ButCostBasisComputed()
    {
        var engine = NewEngine(KnownPricing(1.0m).Object);

        var result = engine.PriceUsage(Line(PricingMode.Byok), Policy(1.3m, 0.50m));

        result.CostBasisUsd.Should().Be(1.000000m); // still computed for reporting
        result.SellPriceUsd.Should().Be(0m);        // no token markup for BYOK
        result.MarginUsd.Should().Be(0m);
        result.PricingMode.Should().Be(PricingMode.Byok);
    }

    [Test]
    public void PriceUsage_UnknownModel_ThrowsPricingUnknownModel_NotSilentZero()
    {
        var mock = new Mock<IProviderPricingService>();
        mock.Setup(m => m.IsKnown(It.IsAny<string>(), It.IsAny<string?>())).Returns(false);
        var engine = NewEngine(mock.Object);

        var act = () => engine.PriceUsage(Line(PricingMode.PlatformProvided), Policy(1.3m, null));

        act.Should().Throw<TammaError>()
            .Which.Code.Should().Be("PRICING.UNKNOWN_MODEL");
        // Compute is never even consulted once IsKnown is false.
        mock.Verify(m => m.Compute(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public void PriceUsage_NullMultiplier_TreatedAsIdentity()
    {
        var engine = NewEngine(KnownPricing(2.0m).Object);

        // fixed-only policy ⇒ multiplier defaults to 1.0 (no markup on the basis).
        var result = engine.PriceUsage(
            Line(PricingMode.PlatformProvided, 0, 0), Policy(null, 0.50m));

        result.SellPriceUsd.Should().Be(2.000000m);
        result.MarginUsd.Should().Be(0m);
    }

    [Test]
    public void PriceUsage_RoundsCostBasisToSixDecimalsEven()
    {
        // 1.2345675 rounds to 1.234568 (6th digit 7 is odd ⇒ round half to even).
        var engine = NewEngine(KnownPricing(1.2345675m).Object);

        var result = engine.PriceUsage(
            Line(PricingMode.PlatformProvided, 0, 0), Policy(1.0m, null));

        result.CostBasisUsd.Should().Be(1.234568m);
        result.SellPriceUsd.Should().Be(1.234568m);
    }

    [Test]
    public void InvoiceUsd_ProjectsToTwoDecimalsEven()
    {
        PricedUsage.InvoiceUsd(1.234568m).Should().Be(1.23m);
        PricedUsage.InvoiceUsd(1.800000m).Should().Be(1.80m);
    }

    // ── Golden-file byte-stability (AC7) ───────────────────────────────────

    private sealed record GoldenScenario(
        string Name,
        string PricingMode,
        string CostBasisUsd,
        string MarginUsd,
        string SellPriceUsd,
        string InvoiceCostBasisUsd,
        string InvoiceMarginUsd,
        string InvoiceSellPriceUsd);

    [Test]
    public void PriceUsage_GoldenScenarios_AreByteStable()
    {
        var occurredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        (string Name, decimal RawCost, PricingMode Mode, decimal? Mult, decimal? Fixed, int In, int Out)[] scenarios =
        {
            ("platform-multiplier-only", 1.0m, PricingMode.PlatformProvided, 1.3m, null, 1000, 500),
            ("platform-fixed-only", 1.0m, PricingMode.PlatformProvided, null, 0.50m, 600_000, 400_000),
            ("platform-both", 1.0m, PricingMode.PlatformProvided, 1.3m, 0.50m, 600_000, 400_000),
            ("byok-zero-token-markup", 1.0m, PricingMode.Byok, 1.3m, null, 1000, 500),
            ("platform-zero-tokens", 0.0m, PricingMode.PlatformProvided, 1.3m, null, 0, 0),
            ("rounding-6dp-even", 1.2345675m, PricingMode.PlatformProvided, 1.0m, null, 0, 0),
        };

        var results = new List<GoldenScenario>();
        foreach (var s in scenarios)
        {
            var engine = NewEngine(KnownPricing(s.RawCost).Object);
            var line = new UsageLine("anthropic", "claude-sonnet-4-20250514", s.In, s.Out, s.Mode, occurredAt);
            var priced = engine.PriceUsage(line, Policy(s.Mult, s.Fixed));

            results.Add(new GoldenScenario(
                s.Name,
                priced.PricingMode.ToString(),
                F6(priced.CostBasisUsd), F6(priced.MarginUsd), F6(priced.SellPriceUsd),
                F2(PricedUsage.InvoiceUsd(priced.CostBasisUsd)),
                F2(PricedUsage.InvoiceUsd(priced.MarginUsd)),
                F2(PricedUsage.InvoiceUsd(priced.SellPriceUsd))));
        }

        var actual = JsonSerializer.Serialize(
            results, new JsonSerializerOptions { WriteIndented = true });

        var expected = File.ReadAllText(GoldenPath());
        Normalize(actual).Should().Be(Normalize(expected));
    }

    private static string F6(decimal v) => v.ToString("F6", CultureInfo.InvariantCulture);
    private static string F2(decimal v) => v.ToString("F2", CultureInfo.InvariantCulture);

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();

    private static string GoldenPath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "golden", "pricing-scenarios.json");
}
