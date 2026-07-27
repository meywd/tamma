using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Tracking;

namespace Tamma.Core.Tests.Tracking;

/// <summary>
/// Story 44-0 AC13 — <c>EstimateScale</c>, count-pinned at 5, hours pointedly
/// absent, plus the estimate/scale coherence rule.
/// </summary>
[TestFixture]
public class EstimateScaleTests
{
    [Test]
    public void Member_count_and_roundtrip()
    {
        Enum.GetValues<EstimateScale>().Should().HaveCount(5);
        Enum.GetValues<EstimateScale>().Select(s => s.ToWire()).Should().Equal(
            "not_used", "linear", "fibonacci", "exponential", "t_shirt");

        foreach (var scale in Enum.GetValues<EstimateScale>())
        {
            EstimateScaleExtensions.TryParse(scale.ToWire(), out var parsed).Should().BeTrue();
            parsed.Should().Be(scale);
            EstimateScaleExtensions.Parse(scale.ToWire()).Should().Be(scale);
        }

        // Ordinal — Linear's camelCase spellings must not parse.
        EstimateScaleExtensions.TryParse("tShirt", out _).Should().BeFalse();
        EstimateScaleExtensions.TryParse("notUsed", out _).Should().BeFalse();
    }

    [Test]
    public void Hours_is_not_a_member()
    {
        // D14: every scale Linear ships pointedly excludes hours — an
        // hours-shaped estimate invites the reading that the number is a
        // commitment. The column is Estimate, not EstimateHours.
        EstimateScaleExtensions.TryParse("hours", out _).Should().BeFalse();

        var act = () => EstimateScaleExtensions.Parse("hours");
        act.Should().Throw<TammaError>().Which.Code.Should().Be("TRACKER.UNKNOWN_ESTIMATE_SCALE");
    }

    [Test]
    public void Estimate_requires_a_scale_that_uses_estimates()
    {
        // The coherence rule (.dev/findings/linear-comparison-against-story-44-0.md,
        // "Found while applying"): (EstimateScale=not_used, Estimate=5) is
        // representable storage-wise and meaningless — the same defect class as
        // (Kind=Bug, Type=Feature). One implementation, here; 44-2 enforces it
        // at the API boundary.
        EstimateScale.NotUsed.AllowsEstimate(5m).Should().BeFalse();
        EstimateScale.NotUsed.AllowsEstimate(0m).Should().BeFalse("any non-null estimate is meaningless without a scale");

        // An estimate is always optional…
        foreach (var scale in Enum.GetValues<EstimateScale>())
            scale.AllowsEstimate(null).Should().BeTrue(because: $"'{scale.ToWire()}' must allow an unset estimate");

        // …and any scale that uses estimates accepts one.
        EstimateScale.Linear.AllowsEstimate(5m).Should().BeTrue();
        EstimateScale.Fibonacci.AllowsEstimate(8m).Should().BeTrue();
        EstimateScale.Exponential.AllowsEstimate(16m).Should().BeTrue();
        EstimateScale.TShirt.AllowsEstimate(3m).Should().BeTrue();
    }
}
