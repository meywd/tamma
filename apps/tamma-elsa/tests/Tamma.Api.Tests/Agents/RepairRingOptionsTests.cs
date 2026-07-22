using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 39-9 (AC2, AC9) — the GLOBAL repair-ring options: default bounds, the
/// hard-cap clamp (no config value can exceed 2), and the default-OFF per-type gate.
/// </summary>
[TestFixture]
public class RepairRingOptionsTests
{
    [Test]
    public void Defaults_MaxRepairTurnsOne_EnabledTypesEmpty()
    {
        var opts = new RepairRingOptions();

        opts.MaxRepairTurns.Should().Be(1, "the default is a single repair turn (AC2)");
        opts.EffectiveMaxRepairTurns.Should().Be(1);
        opts.EnabledDocumentTypes.Should().BeEmpty("the mechanism ships DARK (AC9)");
        RepairRingOptions.HardCap.Should().Be(2);
    }

    [TestCase(5, 2)]   // any large value clamps to the hard cap
    [TestCase(3, 2)]
    [TestCase(2, 2)]
    [TestCase(1, 1)]
    [TestCase(0, 0)]
    [TestCase(-1, 0)]  // negative clamps to zero
    public void EffectiveMaxRepairTurns_ClampsIntoZeroToHardCap(int configured, int effective)
    {
        var opts = new RepairRingOptions { MaxRepairTurns = configured };
        opts.EffectiveMaxRepairTurns.Should().Be(effective,
            "no config value can drive more than the hard cap of 2, nor fewer than 0");
    }

    [Test]
    public void IsEnabledFor_IsCaseInsensitive()
    {
        var opts = new RepairRingOptions { EnabledDocumentTypes = new[] { "decomposition" } };

        opts.IsEnabledFor("decomposition").Should().BeTrue();
        opts.IsEnabledFor("Decomposition").Should().BeTrue("membership is case-insensitive");
        opts.IsEnabledFor("DECOMPOSITION").Should().BeTrue();
        opts.IsEnabledFor("plan").Should().BeFalse("an unlisted type is gated off");
    }

    [Test]
    public void IsEnabledFor_DefaultOptions_AlwaysFalse()
    {
        var opts = new RepairRingOptions();
        opts.IsEnabledFor("decomposition").Should().BeFalse("default OFF for every type (AC9)");
    }
}
