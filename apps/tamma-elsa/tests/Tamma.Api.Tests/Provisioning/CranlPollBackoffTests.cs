using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;

namespace Tamma.Api.Tests.Provisioning;

/// <summary>
/// Unit tests for the exponential poll backoff on <see cref="CranlProvisioningWorkflow"/>.
/// Pure function — no fixture / DB / HTTP.
/// </summary>
[TestFixture]
public class CranlPollBackoffTests
{
    [Test]
    public void Attempt0_ReturnsBaseInterval()
    {
        CranlProvisioningWorkflow.PollBackoff(0)
            .Should().Be(CranlProvisioningWorkflow.PollInterval);
    }

    [Test]
    public void NegativeAttempt_ReturnsBaseInterval()
    {
        CranlProvisioningWorkflow.PollBackoff(-1)
            .Should().Be(CranlProvisioningWorkflow.PollInterval);
    }

    [TestCase(1, 10)]   // 5s * 2^1
    [TestCase(2, 20)]   // 5s * 2^2
    [TestCase(3, 40)]   // 5s * 2^3
    public void EarlyAttempts_DoubleTheBaseInterval(int attempt, int expectedSeconds)
    {
        CranlProvisioningWorkflow.PollBackoff(attempt).TotalSeconds
            .Should().BeApproximately(expectedSeconds, 0.001);
    }

    [TestCase(4)]    // 5s * 16 = 80s -> capped
    [TestCase(10)]
    [TestCase(100)]  // would overflow without the Min(attempt, 20) guard
    public void LargeAttempts_CapAtMax(int attempt)
    {
        CranlProvisioningWorkflow.PollBackoff(attempt)
            .Should().Be(CranlProvisioningWorkflow.PollIntervalMax);
    }

    [Test]
    public void Backoff_IsMonotonicNonDecreasing_AndNeverExceedsMax()
    {
        var prev = System.TimeSpan.Zero;
        for (var attempt = 0; attempt <= 25; attempt++)
        {
            var delay = CranlProvisioningWorkflow.PollBackoff(attempt);
            delay.Should().BeGreaterThanOrEqualTo(prev, "backoff must never shrink");
            delay.Should().BeLessThanOrEqualTo(CranlProvisioningWorkflow.PollIntervalMax);
            prev = delay;
        }
    }
}
