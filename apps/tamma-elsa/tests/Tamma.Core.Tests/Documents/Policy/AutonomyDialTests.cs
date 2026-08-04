using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Documents.Policy;

/// <summary>
/// Invariants of <see cref="AutonomyDial"/> (Story 43-1 AC1/AC10, epic-43
/// decision D3). NOTE: Story 43-1's repo-wide comparison-shaped drift scan
/// (<c>No_autonomy_comparison_restates_a_bound</c>) is NOT here — it can only
/// land together with the <c>AcceptanceRules.cs:85-86</c> production edit that
/// removes the existing literal comparison, which is outside this change's file
/// lane (deferred with the rest of Story 43-1's production/UI/doc edits).
/// </summary>
[TestFixture]
public class AutonomyDialTests
{
    [Test]
    public void Min_is_less_than_Max()
    {
        AutonomyDial.Min.Should().BeLessThan(AutonomyDial.Max);
    }

    [Test]
    public void Min_is_below_the_shipped_default()
    {
        // Story 43-11 AC1: Min (1) and the shipped default dial
        // (AcceptanceDefaults.DefaultAutonomyLevel = 70) are DISTINCT concepts —
        // pinning Min strictly below the default keeps them from re-fusing (they
        // were only incidentally equal before the widen).
        AutonomyDial.Min.Should().BeLessThan(AcceptanceDefaults.DefaultAutonomyLevel);
    }

    [Test]
    public void AlwaysHuman_is_strictly_above_Max()
    {
        AutonomyDial.AlwaysHuman.Should().Be(AutonomyDial.Max + 1);
        AutonomyDial.IsValidLevel(AutonomyDial.AlwaysHuman).Should().BeFalse(
            "AlwaysHuman is a threshold sentinel, never a dial position");
    }

    [Test]
    public void IsValidLevel_accepts_exactly_the_inclusive_range()
    {
        AutonomyDial.IsValidLevel(AutonomyDial.Min - 1).Should().BeFalse();
        AutonomyDial.IsValidLevel(AutonomyDial.Min).Should().BeTrue();
        AutonomyDial.IsValidLevel(AutonomyDial.Max).Should().BeTrue();
        AutonomyDial.IsValidLevel(AutonomyDial.Max + 1).Should().BeFalse();
    }

    [Test]
    public void IsValidThreshold_accepts_AlwaysHuman_and_rejects_Max_plus_2()
    {
        // The sentinel is a CLOSED set member, not an open tail (43-1 D2).
        AutonomyDial.IsValidThreshold(AutonomyDial.Min).Should().BeTrue();
        AutonomyDial.IsValidThreshold(AutonomyDial.Max).Should().BeTrue();
        AutonomyDial.IsValidThreshold(AutonomyDial.AlwaysHuman).Should().BeTrue();
        AutonomyDial.IsValidThreshold(AutonomyDial.Min - 1).Should().BeFalse();
        AutonomyDial.IsValidThreshold(AutonomyDial.Max + 2).Should().BeFalse();
    }

    [Test]
    public void ValidLevels_spans_Min_to_Max_inclusive()
    {
        var levels = AutonomyDial.ValidLevels().ToArray();

        levels.Should().HaveCount(AutonomyDial.Max - AutonomyDial.Min + 1);
        levels.First().Should().Be(AutonomyDial.Min);
        levels.Last().Should().Be(AutonomyDial.Max);
        levels.Should().BeInAscendingOrder();
        levels.Should().OnlyContain(l => AutonomyDial.IsValidLevel(l));
    }
}
