using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Ambiguity;
using Tamma.Activities.Ambiguity.Models;

namespace Tamma.Activities.Tests.Ambiguity;

/// <summary>
/// Story 3.6 — unit coverage for the threshold policy (<see cref="AmbiguityThresholds"/>) that
/// turns a quantitative score into the clarify / proceed routing DECISION (AC6), plus the
/// type / severity label normalisation (<see cref="AmbiguityTypes"/> / <see cref="AmbiguitySeverities"/>).
/// </summary>
[TestFixture]
public class AmbiguityThresholdsTests
{
    [Test]
    public void Resolve_NonPositive_FallsBackToDefault()
    {
        AmbiguityThresholds.Resolve(0m).Should().Be(AmbiguityThresholds.DefaultClarify);
        AmbiguityThresholds.Resolve(-1m).Should().Be(AmbiguityThresholds.DefaultClarify,
            "a threshold of 0 would clarify even a perfectly-clear (score 0) requirement, so " +
            "≤ 0 is treated as 'unset' and falls back to the default");
    }

    [Test]
    public void Resolve_PositiveValue_IsClampedIntoRange()
    {
        AmbiguityThresholds.Resolve(0.3m).Should().Be(0.3m);
        AmbiguityThresholds.Resolve(2m).Should().Be(1m, "a threshold above 1 is clamped to 1");
    }

    [TestCase(0.5, 0.5, true)]   // at threshold → clarify
    [TestCase(0.9, 0.5, true)]   // above threshold → clarify
    [TestCase(0.49, 0.5, false)] // below threshold → proceed
    [TestCase(0.0, 0.5, false)]  // clear requirement → proceed
    public void ShouldClarify_DecidesOnThreshold(double score, double threshold, bool expected)
    {
        AmbiguityThresholds.ShouldClarify((decimal)score, (decimal)threshold)
            .Should().Be(expected);
    }

    [Test]
    public void Clamp01_BoundsValues()
    {
        AmbiguityThresholds.Clamp01(-0.5m).Should().Be(0m);
        AmbiguityThresholds.Clamp01(1.5m).Should().Be(1m);
        AmbiguityThresholds.Clamp01(0.4m).Should().Be(0.4m);
    }

    [TestCase("vague", AmbiguityTypes.Vague)]
    [TestCase("MISSING", AmbiguityTypes.Missing)]
    [TestCase(" Contradictory. ", AmbiguityTypes.Contradictory)]
    [TestCase("implied", AmbiguityTypes.Implicit)]   // synonym fold
    [TestCase("unclear", AmbiguityTypes.Vague)]       // synonym fold
    [TestCase("incomplete", AmbiguityTypes.Missing)]  // synonym fold
    [TestCase("", AmbiguityTypes.Unspecified)]
    [TestCase("gibberish", AmbiguityTypes.Unspecified)]
    public void AmbiguityTypes_Normalize(string raw, string expected)
    {
        AmbiguityTypes.Normalize(raw).Should().Be(expected);
    }

    [TestCase("high", AmbiguitySeverities.High)]
    [TestCase("LOW", AmbiguitySeverities.Low)]
    [TestCase("critical", AmbiguitySeverities.High)] // synonym fold
    [TestCase("trivial", AmbiguitySeverities.Low)]   // synonym fold
    [TestCase("", AmbiguitySeverities.Medium)]        // default
    [TestCase("weird", AmbiguitySeverities.Medium)]   // default
    public void AmbiguitySeverities_Normalize(string raw, string expected)
    {
        AmbiguitySeverities.Normalize(raw).Should().Be(expected);
    }
}
