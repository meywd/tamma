using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Debug;

namespace Tamma.Activities.Tests.Debug;

/// <summary>
/// Regression tests guarding against re-introduction of the simulated
/// refinement path. The original SimulateRefinementResponse returned a fake
/// "what we learned from the failed attempt" narrative plus fabricated
/// hypotheses (e.g. "race condition in async initialization", confidence=0.45)
/// which were then fed back into the next iteration of the debug loop —
/// effectively letting the system gaslight itself with invented evidence.
/// The activity defaulted to UseMock=true, so any deployment that forgot to
/// set Anthropic:UseMock=false silently emitted fake refinements into the
/// audit trail. All refinements must now route through the real engine
/// callback.
/// </summary>
[TestFixture]
public class RefineHypothesisActivityTests
{
    [Test]
    public void RefineHypothesisActivity_ShouldNotExposeAnySimulationMethod()
    {
        // Arrange
        var type = typeof(RefineHypothesisActivity);

        // Act
        var simulationMethods = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name.Contains("Simulate", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Assert
        simulationMethods.Should().BeEmpty(
            "RefineHypothesisActivity must not contain any simulated refinement path — "
            + "fake refinements feed back into the debug loop and poison subsequent attempts");
    }

    [Test]
    public void RefineHypothesisActivity_ShouldNotReferenceUseMockConfig()
    {
        // Arrange
        var type = typeof(RefineHypothesisActivity);

        // Act
        var mockMethods = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name.Contains("Mock", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Assert
        mockMethods.Should().BeEmpty(
            "RefineHypothesisActivity must not expose any Mock-named method — production uses real LLM only");
    }
}
