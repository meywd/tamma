using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Pricing;
using Tamma.Core.Enums;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-6 (AC8, AC13) — the pure, non-enforcing headroom calc shared by
/// enforcement + both dashboards. Covers under / at-limit / over / unlimited /
/// zero-limit / usage-unavailable matrix so the one shared computation can never
/// diverge between consumers.
/// </summary>
[TestFixture]
public class CheckHeadroomTests
{
    private const EntitlementMetricKey Metric = EntitlementMetricKey.WorkflowRuns;

    [Test]
    public void Under_Limit_HasRemaining_NotOver()
    {
        var h = EntitlementDefaults.ComputeHeadroom(Metric, limitValue: 100, currentUsage: 40);

        h.LimitValue.Should().Be(100);
        h.CurrentUsage.Should().Be(40);
        h.Remaining.Should().Be(60);
        h.IsOver.Should().BeFalse();
        h.OveragePercent.Should().BeApproximately(40d, 0.0001);
    }

    [Test]
    public void At_Limit_ZeroRemaining_NotOver()
    {
        var h = EntitlementDefaults.ComputeHeadroom(Metric, limitValue: 100, currentUsage: 100);

        h.Remaining.Should().Be(0);
        h.IsOver.Should().BeFalse();
        h.OveragePercent.Should().BeApproximately(100d, 0.0001);
    }

    [Test]
    public void Over_Limit_ClampsRemainingToZero_IsOver()
    {
        var h = EntitlementDefaults.ComputeHeadroom(Metric, limitValue: 100, currentUsage: 130);

        h.Remaining.Should().Be(0, "remaining never goes negative");
        h.IsOver.Should().BeTrue();
        h.OveragePercent.Should().BeApproximately(130d, 0.0001);
    }

    [Test]
    public void Unlimited_NullLimit_NeverOver_RemainingNull_EvenAtHugeUsage()
    {
        var h = EntitlementDefaults.ComputeHeadroom(Metric, limitValue: null, currentUsage: long.MaxValue);

        h.LimitValue.Should().BeNull();
        h.Remaining.Should().BeNull();
        h.IsOver.Should().BeFalse();
        h.OveragePercent.Should().BeNull();
        h.CurrentUsage.Should().Be(long.MaxValue);
    }

    [Test]
    public void ZeroLimit_OveragePercentNull_ButIsOverWhenUsagePositive()
    {
        var over = EntitlementDefaults.ComputeHeadroom(Metric, limitValue: 0, currentUsage: 5);
        over.OveragePercent.Should().BeNull("division-by-zero guard");
        over.IsOver.Should().BeTrue();
        over.Remaining.Should().Be(0);

        var atZero = EntitlementDefaults.ComputeHeadroom(Metric, limitValue: 0, currentUsage: 0);
        atZero.IsOver.Should().BeFalse();
        atZero.OveragePercent.Should().BeNull();
    }

    [Test]
    public void UsageUnavailable_NullUsage_DegradesGracefully()
    {
        var h = EntitlementDefaults.ComputeHeadroom(Metric, limitValue: 100, currentUsage: null);

        h.LimitValue.Should().Be(100);
        h.CurrentUsage.Should().BeNull();
        h.Remaining.Should().BeNull();
        h.IsOver.Should().BeFalse();
        h.OveragePercent.Should().BeNull();
    }
}
