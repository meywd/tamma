using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Analytics;

namespace Tamma.Activities.Tests.Analytics;

/// <summary>
/// Story 28-10 — unit tests for the retention policy embedded in
/// <see cref="PurgeStaleAnalyticsActivity"/> (the
/// <c>PURGE_ANALYTICS_HOURLY</c> sweeper). The set-based
/// <c>ExecuteDeleteAsync</c> itself is relational-only (covered by the
/// Postgres integration job, same as
/// <c>PlatformWebhookDeliveryRepository.CleanupOlderThanAsync</c>); the
/// 13-month cutoff arithmetic is pure and pinned here.
/// </summary>
[TestFixture]
public class PurgeStaleAnalyticsActivityTests
{
    [Test]
    public void DefaultRetentionMonths_Is13()
    {
        PurgeStaleAnalyticsActivity.DefaultRetentionMonths.Should().Be(13);
    }

    [Test]
    public void ComputeCutoff_SubtractsRetentionWindow_FromNow()
    {
        var now = new DateTime(2026, 06, 05, 14, 0, 0, DateTimeKind.Utc);

        var cutoff = PurgeStaleAnalyticsActivity.ComputeCutoff(now, 13);

        cutoff.Should().Be(new DateTime(2025, 05, 05, 14, 0, 0, DateTimeKind.Utc));
        cutoff.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Test]
    public void ComputeCutoff_NormalisesNonUtcInputToUtc()
    {
        var localish = new DateTime(2026, 06, 05, 14, 0, 0, DateTimeKind.Unspecified);

        var cutoff = PurgeStaleAnalyticsActivity.ComputeCutoff(localish, 13);

        cutoff.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Test]
    public void ComputeCutoff_ClampsNonPositiveRetention_ToDefault()
    {
        // A misconfigured 0 / negative window must never delete the
        // present — it falls back to the 13-month default rather than
        // wiping live buckets.
        var now = new DateTime(2026, 06, 05, 0, 0, 0, DateTimeKind.Utc);

        var zero = PurgeStaleAnalyticsActivity.ComputeCutoff(now, 0);
        var negative = PurgeStaleAnalyticsActivity.ComputeCutoff(now, -5);
        var expected = now.AddMonths(-PurgeStaleAnalyticsActivity.DefaultRetentionMonths);

        zero.Should().Be(expected);
        negative.Should().Be(expected);
    }
}
