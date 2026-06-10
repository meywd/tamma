using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Debug;

namespace Tamma.Activities.Tests.Debug;

/// <summary>
/// Regression tests guarding against re-introduction of the simulated diagnosis
/// path. The original SimulateDiagnosisResponse returned hard-coded fake
/// hypotheses ("Logic error in condition evaluation", confidence=0.75 etc.)
/// that leaked into the audit trail and the iterative debug loop, poisoning
/// downstream fix attempts. The activity defaulted to UseMock=true, meaning
/// any deployment that forgot to set Anthropic:UseMock=false silently emitted
/// fabricated LLM output. All diagnoses must now route through the real
/// engine callback or direct Anthropic API.
/// </summary>
[TestFixture]
public class AIDiagnosisActivityTests
{
    [Test]
    public void AIDiagnosisActivity_ShouldNotExposeAnySimulationMethod()
    {
        // Arrange
        var type = typeof(AIDiagnosisActivity);

        // Act
        var simulationMethods = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name.Contains("Simulate", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Assert
        simulationMethods.Should().BeEmpty(
            "AIDiagnosisActivity must not contain any simulated diagnosis path — "
            + "fake hypotheses corrupt the audit trail and poison the iterative debug loop");
    }

    [Test]
    public void AIDiagnosisActivity_ShouldNotReferenceUseMockConfig()
    {
        // Arrange
        var type = typeof(AIDiagnosisActivity);

        // Act
        var mockMethods = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name.Contains("Mock", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Assert
        mockMethods.Should().BeEmpty(
            "AIDiagnosisActivity must not expose any Mock-named method — production uses real LLM only");
    }
}
