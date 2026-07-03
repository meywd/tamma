using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Enums;

namespace Tamma.Core.Tests.Enums;

/// <summary>
/// Story 34-3 / 35-2 — the shared <see cref="MetricBillingMode"/> canonical
/// token round-trips to the lowercase wire form (<c>"platform"</c>/<c>"byok"</c>)
/// used by every reader (owner column, diagnostic column, DCB tag,
/// <c>CredentialSource.ToTag()</c>, analytics <c>ResolveCostBasis</c>).
/// </summary>
[TestFixture]
public class MetricBillingModeTests
{
    [Test]
    public void ToToken_ProducesLowercaseWireTokens()
    {
        MetricBillingMode.PlatformProvided.ToToken().Should().Be("platform");
        MetricBillingMode.Byok.ToToken().Should().Be("byok");
    }

    [Test]
    public void PlatformProvided_IsTheZeroOrdinalDefault()
    {
        // The safe default (single-user / rows-absent) resolves to platform.
        ((int)MetricBillingMode.PlatformProvided).Should().Be(0);
    }

    [TestCase("byok", MetricBillingMode.Byok)]
    [TestCase("BYOK", MetricBillingMode.Byok)]
    [TestCase("platform", MetricBillingMode.PlatformProvided)]
    [TestCase("PlatformProvided", MetricBillingMode.PlatformProvided)]
    [TestCase(" Byok ", MetricBillingMode.Byok)]
    public void TryParseToken_AcceptsTokensAndMemberNames(string token, MetricBillingMode expected)
    {
        MetricBillingModeExtensions.TryParseToken(token, out var mode).Should().BeTrue();
        mode.Should().Be(expected);
    }

    [TestCase("")]
    [TestCase(null)]
    [TestCase("nonsense")]
    public void TryParseToken_RejectsOutOfDomain_WithPlatformOutParam(string? token)
    {
        MetricBillingModeExtensions.TryParseToken(token, out var mode).Should().BeFalse();
        // Out-param is the safe default; the CALLER decides fail-loud vs default.
        mode.Should().Be(MetricBillingMode.PlatformProvided);
    }
}
