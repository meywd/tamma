using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Debug;

namespace Tamma.Activities.Tests.Debug;

/// <summary>
/// Regression tests guarding against re-introduction of the simulated
/// regression-test path. The original SimulateTestResponse returned a
/// hard-coded `expect(true).toBe(true)` test claiming `fails_as_expected =
/// true`, which (a) reproduces no actual bug and (b) lies in the audit trail
/// about regression coverage. The activity defaulted to UseMock=true, so any
/// deployment that forgot to set Anthropic:UseMock=false would silently
/// generate fake regression tests that always passed — defeating the entire
/// purpose of bug-investigation mode. All regression tests must now route
/// through the real engine callback.
/// </summary>
[TestFixture]
public class WriteRegressionTestActivityTests
{
    [Test]
    public void WriteRegressionTestActivity_ShouldNotExposeAnySimulationMethod()
    {
        // Arrange
        var type = typeof(WriteRegressionTestActivity);

        // Act
        var simulationMethods = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name.Contains("Simulate", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Assert
        simulationMethods.Should().BeEmpty(
            "WriteRegressionTestActivity must not contain any simulated test-generation path — "
            + "fake regression tests that always pass corrupt the audit trail and defeat bug-investigation mode");
    }

    [Test]
    public void WriteRegressionTestActivity_ShouldNotReferenceUseMockConfig()
    {
        // Arrange
        var type = typeof(WriteRegressionTestActivity);

        // Act
        var mockMethods = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name.Contains("Mock", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Assert
        mockMethods.Should().BeEmpty(
            "WriteRegressionTestActivity must not expose any Mock-named method — production uses real LLM only");
    }
}
