using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Services.Analytics;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-10 — verify the fact-table-first read path on
/// <see cref="PlatformAnalyticsService"/>. When
/// <c>platform_analytics_hourly</c> carries a recent row, the service
/// should return workflow / cost tiles sourced from the fact table
/// (not from the live <c>workflow_instances</c> / <c>domain_events</c>
/// query). When the fact table is empty or stale, it falls back.
/// </summary>
[TestFixture]
public class PlatformAnalyticsServiceFactTableTests
{
    private static readonly DateTime FixedNow =
        new(2026, 04, 18, 12, 00, 00, DateTimeKind.Utc);

    private ControlPlaneDbContext _cp = null!;
    private DbContextOptions<TenantDbContext> _tenantOptions = null!;
    private ITenantDbContextFactory _tenantFactory = null!;
    private PlatformAnalyticsService _sut = null!;
    private FakeTimeProvider _clock = null!;

    [SetUp]
    public void SetUp()
    {
        // Story 28-1 PR D: WorkflowInstances + DomainEvents moved off CP
        // to the per-tenant DB. Tests need both contexts now — CP for the
        // tenants/fact table and the tenant factory for the workflow /
        // event seeds.
        var cpOptions = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase($"cp-fact-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _cp = new ControlPlaneDbContext(cpOptions);

        _tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase($"tenant-fact-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _tenantFactory = new TestTenantDbContextFactory(_tenantOptions);

        _clock = new FakeTimeProvider(FixedNow);
        _sut = new PlatformAnalyticsService(_cp, _tenantFactory, _clock);
    }

    [TearDown]
    public void TearDown()
    {
        _cp.Dispose();
    }

    [Test]
    public async Task ShouldPreferFactTable_ReturnsFalse_WhenTableEmpty()
    {
        var prefer = await _sut.ShouldPreferFactTableAsync(FixedNow, CancellationToken.None);
        prefer.Should().BeFalse("empty fact table must fall back to live aggregation");
    }

    [Test]
    public async Task ShouldPreferFactTable_ReturnsTrue_WhenRecentRowExists()
    {
        await SeedFactRowAsync(
            hour: FixedNow.AddHours(-1),
            tenantId: Guid.NewGuid());

        var prefer = await _sut.ShouldPreferFactTableAsync(FixedNow, CancellationToken.None);
        prefer.Should().BeTrue("a row less than 90 minutes old is fresh");
    }

    [Test]
    public async Task ShouldPreferFactTable_ReturnsFalse_WhenStale()
    {
        // Most-recent row is > 90 minutes old → stale.
        await SeedFactRowAsync(
            hour: FixedNow.AddHours(-3),
            tenantId: Guid.NewGuid());

        var prefer = await _sut.ShouldPreferFactTableAsync(FixedNow, CancellationToken.None);
        prefer.Should().BeFalse("rollup > 90 minutes late should fall back");
    }

    [Test]
    public async Task GetSummary_UsesFactTable_WhenRecentRowsExist()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Three hours of per-tenant rows in the last 24h.
        await SeedFactRowAsync(
            hour: FixedNow.AddHours(-1),
            tenantId: tenantA,
            workflowsStarted: 10,
            workflowsCompleted: 8,
            workflowsFailed: 1,
            costUsd: 0.50m);

        await SeedFactRowAsync(
            hour: FixedNow.AddHours(-2),
            tenantId: tenantA,
            workflowsStarted: 5,
            workflowsCompleted: 4,
            workflowsFailed: 0,
            costUsd: 0.25m);

        await SeedFactRowAsync(
            hour: FixedNow.AddHours(-1),
            tenantId: tenantB,
            workflowsStarted: 3,
            workflowsCompleted: 3,
            workflowsFailed: 0,
            costUsd: 0.10m);

        var summary = await _sut.GetSummaryAsync();

        summary.Workflows.Last24h.Completed.Should().Be(15);  // 8 + 4 + 3
        summary.Workflows.Last24h.Failed.Should().Be(1);
        // Running = started - completed - failed = (10+5+3) - 15 - 1 = 2
        summary.Workflows.Last24h.Running.Should().Be(2);

        summary.Costs.Last24hUsd.Should().Be(0.85m);
    }

    [Test]
    public async Task GetSummary_FallsBackToLive_WhenFactTableEmpty()
    {
        // No fact-table rows — populate the live sources.
        // Story 28-1 PR D: workflow_instances + domain_events live on the
        // tenant DB. Seed the tenant on CP first, then route per-tenant
        // seeds through the factory.
        var tenantId = Guid.NewGuid();
        _cp.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = $"t-{tenantId:N}",
            Slug = $"t-{tenantId:N}",
            Type = "personal",
            CreatedAt = FixedNow,
            UpdatedAt = FixedNow,
        });
        await _cp.SaveChangesAsync();

        await using var tdb = await _tenantFactory.CreateAsync(tenantId);
        tdb.WorkflowInstances.Add(new WorkflowInstance
        {
            Id = Guid.NewGuid(),
            DefinitionId = Guid.NewGuid(),
            TenantId = tenantId,
            Status = "completed",
            Variables = "{}",
            CreatedAt = FixedNow.AddHours(-1),
            UpdatedAt = FixedNow.AddHours(-1),
        });
        tdb.DomainEvents.Add(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = "LLM.CALL.SUCCESS",
            TenantId = tenantId,
            Tags = "{}",
            Metadata = "{}",
            Data = "{\"costUsd\":0.75}",
            CreatedAt = FixedNow.AddHours(-1),
        });
        await tdb.SaveChangesAsync();

        var summary = await _sut.GetSummaryAsync();

        // Live path returned actual values, not zeros from the empty fact table.
        summary.Workflows.Last24h.Completed.Should().Be(1);
        summary.Costs.Last24hUsd.Should().Be(0.75m);
    }

    [Test]
    public async Task GetSummary_FactTable_ExcludesPlatformWideRowFromWorkflowSum()
    {
        var tenantA = Guid.NewGuid();

        await SeedFactRowAsync(
            hour: FixedNow.AddHours(-1),
            tenantId: tenantA,
            workflowsCompleted: 5);

        // Platform-wide row — must NOT contribute to workflow counts.
        await SeedFactRowAsync(
            hour: FixedNow.AddHours(-1),
            tenantId: null,
            workflowsCompleted: 999,
            agentDispatches: 12,
            activeTenantsAtHourEnd: 1);

        var summary = await _sut.GetSummaryAsync();

        summary.Workflows.Last24h.Completed.Should().Be(5,
            "platform-wide rows must not inflate workflow totals");

        // Agent dispatches SUM the tenant row + the platform-wide row
        // (both are legitimate signals).
        summary.AgentDispatches.Last24h.Attempted.Should().Be(12);
    }

    [Test]
    public async Task GetSummary_FactTable_WindowedCorrectlyFor7dAnd30d()
    {
        var tenantA = Guid.NewGuid();

        await SeedFactRowAsync(
            hour: FixedNow.AddHours(-1),
            tenantId: tenantA,
            workflowsCompleted: 1);

        await SeedFactRowAsync(
            hour: FixedNow.AddDays(-3),
            tenantId: tenantA,
            workflowsCompleted: 10);

        await SeedFactRowAsync(
            hour: FixedNow.AddDays(-20),
            tenantId: tenantA,
            workflowsCompleted: 100);

        await SeedFactRowAsync(
            hour: FixedNow.AddDays(-40),
            tenantId: tenantA,
            workflowsCompleted: 1000);

        var summary = await _sut.GetSummaryAsync();

        summary.Workflows.Last24h.Completed.Should().Be(1);
        summary.Workflows.Last7d.Completed.Should().Be(11);
        summary.Workflows.Last30d.Completed.Should().Be(111,
            "40-day-old rows are outside the 30-day window");
    }

    [Test]
    public async Task GetSummary_FactTable_RunningClampedAtZero()
    {
        var tenantA = Guid.NewGuid();

        // completed + failed > started — running would be negative without clamp.
        await SeedFactRowAsync(
            hour: FixedNow.AddHours(-1),
            tenantId: tenantA,
            workflowsStarted: 3,
            workflowsCompleted: 5,
            workflowsFailed: 2);

        var summary = await _sut.GetSummaryAsync();

        summary.Workflows.Last24h.Running.Should().Be(0,
            "clamp prevents negative 'running' when events straddle bucket boundaries");
    }

    private async Task SeedFactRowAsync(
        DateTime hour,
        Guid? tenantId,
        long workflowsStarted = 0,
        long workflowsCompleted = 0,
        long workflowsFailed = 0,
        long agentDispatches = 0,
        long tokensIn = 0,
        long tokensOut = 0,
        decimal costUsd = 0m,
        int activeTenantsAtHourEnd = 0)
    {
        _cp.PlatformAnalyticsHourly.Add(new PlatformAnalyticsHourly
        {
            Id = Guid.NewGuid(),
            Hour = hour,
            TenantId = tenantId,
            WorkflowsStarted = workflowsStarted,
            WorkflowsCompleted = workflowsCompleted,
            WorkflowsFailed = workflowsFailed,
            AgentDispatches = agentDispatches,
            TokensIn = tokensIn,
            TokensOut = tokensOut,
            CostUsd = costUsd,
            ActiveTenantsAtHourEnd = activeTenantsAtHourEnd,
            ComputedAt = hour.AddMinutes(5),
        });
        await _cp.SaveChangesAsync();
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTime _utcNow;
        public FakeTimeProvider(DateTime utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => new(_utcNow, TimeSpan.Zero);
    }
}
