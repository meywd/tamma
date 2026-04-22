using NUnit.Framework;
using Tamma.Api.Services.Providers;

namespace Tamma.Api.Tests.ProviderSession;

/// <summary>
/// Verify that the ported pricing table (finding 004) computes the same
/// USD-per-token rates as the TS <c>CostCalculator.calculate</c> for the
/// dominant providers. Sanity-tests cover the alias map, prefix matching,
/// and the unknown-model zero-cost fallback.
/// </summary>
[TestFixture]
public class ProviderPricingServiceTests
{
    private ProviderPricingService _svc = null!;

    [SetUp]
    public void Setup() => _svc = new ProviderPricingService();

    [Test]
    public void AnthropicSonnet4_ReportsKnownRate()
    {
        // 1000 input + 500 output @ $3/M input + $15/M output = $0.003 + $0.0075 = $0.0105
        var cost = _svc.Compute("anthropic", "claude-sonnet-4-20250514", 1000, 500);
        Assert.That(cost, Is.EqualTo(0.0105m));
    }

    [Test]
    public void OpenAiGpt4oMini_ReportsKnownRate()
    {
        // 10_000 input @ $0.15/M + 2_000 output @ $0.60/M = $0.0015 + $0.0012 = $0.0027
        var cost = _svc.Compute("openai", "gpt-4o-mini", 10_000, 2_000);
        Assert.That(cost, Is.EqualTo(0.0027m));
    }

    [Test]
    public void GeminiAlias_ResolvesToGoogleTable()
    {
        // gemini → google in the alias table; gemini-1.5-flash @ $0.075/M input + $0.30/M output
        var cost = _svc.Compute("gemini", "gemini-1.5-flash", 1_000_000, 1_000_000);
        Assert.That(cost, Is.EqualTo(0.075m + 0.30m));
    }

    [Test]
    public void ClaudeCodeAlias_UsesAnthropicRates()
    {
        var anthropicCost = _svc.Compute("anthropic", "claude-sonnet-4-20250514", 1000, 500);
        var claudeCodeCost = _svc.Compute("claude-code", "claude-sonnet-4-20250514", 1000, 500);
        Assert.That(claudeCodeCost, Is.EqualTo(anthropicCost));
    }

    [Test]
    public void UnknownProvider_ReturnsZero()
    {
        var cost = _svc.Compute("zenith-9000", "model-x", 1000, 500);
        Assert.That(cost, Is.EqualTo(0m));
    }

    [Test]
    public void UnknownModelOnKnownProvider_ReturnsZero()
    {
        var cost = _svc.Compute("openai", "gpt-99-nope", 1000, 500);
        Assert.That(cost, Is.EqualTo(0m));
    }

    [Test]
    public void NegativeTokenCounts_ClampToZero()
    {
        var cost = _svc.Compute("anthropic", "claude-sonnet-4-20250514", -100, -200);
        Assert.That(cost, Is.EqualTo(0m));
    }

    [Test]
    public void IsKnown_ReturnsTrueForKnownPair()
    {
        Assert.That(_svc.IsKnown("openai", "gpt-4o"), Is.True);
        Assert.That(_svc.IsKnown("anthropic", "claude-3-5-haiku-20241022"), Is.True);
    }

    [Test]
    public void IsKnown_ReturnsFalseForUnknownPair()
    {
        Assert.That(_svc.IsKnown("zenith-9000", "model"), Is.False);
        Assert.That(_svc.IsKnown("openai", "gpt-99-nope"), Is.False);
    }

    [Test]
    public void ModelPrefixMatch_ResolvesShortHandToFullVersion()
    {
        // Caller passes "claude-sonnet-4" without the date suffix; prefix match
        // resolves to claude-sonnet-4-20250514.
        var cost = _svc.Compute("anthropic", "claude-sonnet-4", 1000, 500);
        Assert.That(cost, Is.EqualTo(0.0105m));
    }
}
