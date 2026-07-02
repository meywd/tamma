using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Analytics;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Activities.Tests.Analytics;

/// <summary>
/// Story 36-2 (AC7) — checkpoint advance + crash-resume for the dimensional
/// projection. The <see cref="AnalyticsProjectionCheckpoint"/> row advances to
/// the max folded <see cref="DomainEvent.SequenceNumber"/>; because the upsert
/// is a whole-bucket overwrite, re-folding un-checkpointed events never
/// double-counts. (Relational NULLS-NOT-DISTINCT collision is proven in the
/// Postgres Testcontainer suite.)
/// </summary>
[TestFixture]
public class AnalyticsProjectionCheckpointTests
{
    private static readonly DateTime Hour = new(2026, 04, 18, 12, 00, 00, DateTimeKind.Utc);

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

    private static DomainEvent Llm(long seq, string provider, decimal cost) => new()
    {
        Id = Guid.NewGuid(),
        Type = "LLM.CALL.SUCCESS",
        CreatedAt = Hour.AddMinutes(seq),
        SequenceNumber = seq,
        Tags = JsonSerializer.Serialize(new Dictionary<string, string?> { ["provider"] = provider }),
        Metadata = "{}",
        Data = JsonSerializer.Serialize(new { costUsd = cost, inputTokens = 10, outputTokens = 5 }),
    };

    private async Task RunAsync(Guid tenantId, bool reset = false) =>
        await ComputeTenantDimensionalRollupActivity.ComputeAsync(
            _tenantFactory, _publisher.Object, tenantId, Hour, new FixedMarginPricing(0m), reset, null,
            CancellationToken.None);

    [Test]
    public async Task Checkpoint_Absent_StartsAtZero_ThenAdvances()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.AddRange(Llm(5, "anthropic", 0.10m), Llm(12, "anthropic", 0.10m));
        await db.SaveChangesAsync();

        await RunAsync(tenantId);

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var cp = await verify.AnalyticsProjectionCheckpoints.SingleAsync();
        cp.Stream.Should().Be(AnalyticsProjectionCheckpoint.DimensionalStream);
        cp.LastSequenceNumber.Should().Be(12);
    }

    [Test]
    public async Task Checkpoint_NeverRegresses_OnHappyPathReRun()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.Add(Llm(50, "anthropic", 0.10m));
        await db.SaveChangesAsync();

        await RunAsync(tenantId);
        // A second (non-reset) run of the same bucket keeps the checkpoint at 50.
        await RunAsync(tenantId);

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var cp = await verify.AnalyticsProjectionCheckpoints.SingleAsync();
        cp.LastSequenceNumber.Should().Be(50);
    }

    [Test]
    public async Task HighWater_SecondRunWithNoNewEvents_SkipsRecompute()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.AddRange(Llm(5, "anthropic", 0.10m), Llm(10, "anthropic", 0.10m));
        await db.SaveChangesAsync();

        await RunAsync(tenantId);

        // Corrupt the projected row with a sentinel. If the second run RECOMPUTES,
        // the whole-bucket overwrite resets it; if it correctly SKIPS (no event
        // with SequenceNumber > checkpoint 10), the sentinel survives untouched.
        var mutate = await _tenantFactory.CreateAsync(tenantId);
        var row = await mutate.AnalyticsUsageHourly.SingleAsync();
        row.CostUsd = 999m;
        await mutate.SaveChangesAsync();

        await RunAsync(tenantId); // no new events → must skip

        var verify = await _tenantFactory.CreateAsync(tenantId);
        (await verify.AnalyticsUsageHourly.SingleAsync()).CostUsd
            .Should().Be(999m, "no new events → recompute skipped → sentinel untouched");
        (await verify.AnalyticsProjectionCheckpoints.SingleAsync()).LastSequenceNumber
            .Should().Be(10, "checkpoint unchanged when nothing newer is folded");
    }

    [Test]
    public async Task HighWater_ThirdRunWithNewEvent_Recomputes()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.Add(Llm(5, "anthropic", 0.10m));
        await db.SaveChangesAsync();

        await RunAsync(tenantId); // checkpoint → 5

        // A NEW event (SequenceNumber 20 > checkpoint 5) must trigger a recompute
        // that re-derives the whole bucket (0.10 + 0.30) and advances the checkpoint.
        var add = await _tenantFactory.CreateAsync(tenantId);
        add.DomainEvents.Add(Llm(20, "anthropic", 0.30m));
        await add.SaveChangesAsync();

        await RunAsync(tenantId);

        var verify = await _tenantFactory.CreateAsync(tenantId);
        (await verify.AnalyticsUsageHourly.SingleAsync()).CostUsd
            .Should().Be(0.40m, "a new event forces the whole-bucket recompute");
        (await verify.AnalyticsProjectionCheckpoints.SingleAsync()).LastSequenceNumber
            .Should().Be(20, "checkpoint advances to the new max SequenceNumber");
    }

    [Test]
    public async Task CrashResume_ReFoldsSameBucket_NoDoubleCount()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.AddRange(Llm(1, "anthropic", 0.10m), Llm(2, "anthropic", 0.20m));
        await db.SaveChangesAsync();

        await RunAsync(tenantId);
        // Simulate a crash-resume: re-run re-folds the whole bucket.
        await RunAsync(tenantId, reset: true);

        var verify = await _tenantFactory.CreateAsync(tenantId);
        var row = await verify.AnalyticsUsageHourly.SingleAsync();
        row.CostUsd.Should().Be(0.30m, "whole-bucket overwrite absorbs the re-fold — no double-count");
    }
}
