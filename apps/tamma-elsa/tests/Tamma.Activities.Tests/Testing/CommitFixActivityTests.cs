using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Testing;
using Tamma.Activities.Testing.Models;

namespace Tamma.Activities.Tests.Testing;

/// <summary>
/// Regression tests guarding against re-introduction of the simulated commit
/// path. The original SimulateCommitFix returned random success rates and
/// fabricated commit SHAs (e.g. "abc1234"), which leaked into downstream audit
/// events and corrupted the audit trail. All commits must route through the
/// real engine callback.
/// </summary>
[TestFixture]
public class CommitFixActivityTests
{
    [Test]
    public void CommitFixActivity_ShouldNotExposeAnySimulationMethod()
    {
        // Arrange
        var type = typeof(CommitFixActivity);

        // Act
        var simulationMethods = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name.Contains("Simulate", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Assert
        simulationMethods.Should().BeEmpty(
            "CommitFixActivity must not contain any simulated commit path — fake commit SHAs corrupt the audit trail");
    }

    [Test]
    public void CommitFixActivity_ShouldExposeOnlyRealCommitImplementation()
    {
        // Arrange
        var type = typeof(CommitFixActivity);

        // Act
        var commitMethods = type
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name.Contains("Commit", StringComparison.OrdinalIgnoreCase)
                        && !m.IsSpecialName)
            .Select(m => m.Name)
            .ToList();

        // Assert
        commitMethods.Should().Contain("CommitRealFix",
            "the real-engine commit path is the only sanctioned commit implementation");
        commitMethods.Should().NotContain(n => n.Contains("Simulate", StringComparison.OrdinalIgnoreCase));
        commitMethods.Should().NotContain(n => n.Contains("Mock", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void CommitFixResult_DefaultsAreFailureSafe()
    {
        // Arrange & Act
        var result = new CommitFixResult();

        // Assert — a freshly-constructed result must never look like a real successful commit
        result.Success.Should().BeFalse();
        result.CommitSha.Should().BeNull();
    }
}
