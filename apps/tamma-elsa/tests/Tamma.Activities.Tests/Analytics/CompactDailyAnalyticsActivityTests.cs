using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Analytics;
using Tamma.Core.Enums;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Activities.Tests.Analytics;

/// <summary>
/// Story 36-2 — unit tests for
/// <see cref="CompactDailyAnalyticsActivity.CompactAsync"/>: lossless hourly →
/// daily GROUP BY sum, and idempotent re-compaction.
/// </summary>
[TestFixture]
public class CompactDailyAnalyticsActivityTests
{
    private static readonly DateTime Day = new(2026, 04, 18, 0, 0, 0, DateTimeKind.Utc);

    private FakeTenantDbContextFactory _tenantFactory = null!;
    private Mock<IPlatformEventPublisher> _publisher = null!;
    private List<IDisposable> _opened = null!;

    [SetUp]
    public void SetUp()
    {
        _opened = new List<IDisposable>();
        _tenantFactory = new FakeTenantDbContextFactory(_opened);
        _publisher = new Mock<IPlatformEventPublisher>();
        _publisher
            .Setup(p => p.AppendAndPublishAsync(It.IsAny<PlatformEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformEvent evt, CancellationToken _) => evt);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var ctx in _opened) ctx.Dispose();
    }

    private static AnalyticsUsageHourly HourRow(DateTime hour, string provider, long tin, decimal cost) => new()
    {
        Id = Guid.NewGuid(),
        Hour = hour,
        Provider = provider,
        CostBasis = CostBasis.Platform,
        TokensIn = tin,
        TokensOut = tin / 2,
        CostUsd = cost,
        PlatformBilledUsd = cost,
        WorkflowsStarted = 1,
        AgentDispatches = 1,
        ComputedAt = DateTime.UtcNow,
    };

    [Test]
    public async Task CompactAsync_RollsHourlyIntoDaily_Losslessly()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        // Three hours of the same provider tuple + one different provider.
        db.AnalyticsUsageHourly.AddRange(
            HourRow(Day.AddHours(0), "anthropic", 100, 0.10m),
            HourRow(Day.AddHours(1), "anthropic", 200, 0.20m),
            HourRow(Day.AddHours(2), "anthropic", 300, 0.30m),
            HourRow(Day.AddHours(3), "openai", 50, 0.05m),
            // Next day — excluded.
            HourRow(Day.AddDays(1), "anthropic", 999, 9.99m));
        await db.SaveChangesAsync();

        await CompactDailyAnalyticsActivity.CompactAsync(
            _tenantFactory, _publisher.Object, tenantId, Day, null, CancellationToken.None);

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var daily = await verify.AnalyticsUsageDaily.ToListAsync();

        daily.Should().HaveCount(2);
        var anthropic = daily.Single(r => r.Provider == "anthropic");
        anthropic.Day.Should().Be(Day);
        anthropic.TokensIn.Should().Be(600, "3 hours summed losslessly");
        anthropic.CostUsd.Should().Be(0.60m);
        anthropic.WorkflowsStarted.Should().Be(3);
        anthropic.AgentDispatches.Should().Be(3);

        daily.Single(r => r.Provider == "openai").TokensIn.Should().Be(50);
    }

    [Test]
    public async Task CompactAsync_Idempotent_ReRunOverwritesNotDuplicates()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.AnalyticsUsageHourly.Add(HourRow(Day.AddHours(0), "anthropic", 100, 0.10m));
        await db.SaveChangesAsync();

        await CompactDailyAnalyticsActivity.CompactAsync(
            _tenantFactory, _publisher.Object, tenantId, Day, null, CancellationToken.None);
        await CompactDailyAnalyticsActivity.CompactAsync(
            _tenantFactory, _publisher.Object, tenantId, Day, null, CancellationToken.None);

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var daily = await verify.AnalyticsUsageDaily.ToListAsync();
        daily.Should().ContainSingle("re-compaction upserts on the daily business key");
        daily[0].TokensIn.Should().Be(100);
    }
}
