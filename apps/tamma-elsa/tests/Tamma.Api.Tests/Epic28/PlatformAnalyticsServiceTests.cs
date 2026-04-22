using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Services.Analytics;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-10 — unit tests for <see cref="PlatformAnalyticsService"/>.
/// Uses EF InMemory + a fixed TimeProvider for deterministic windows.
/// </summary>
[TestFixture]
public class PlatformAnalyticsServiceTests
{
    private static readonly DateTime FixedNow =
        new(2026, 04, 18, 12, 00, 00, DateTimeKind.Utc);

    private ControlPlaneDbContext _cp = null!;
    // Wave A.5 post-merge: DomainEvents + WorkflowInstances DbSets live on
    // ControlPlaneDbContext now as legacy-shared tables. _app is an alias
    // for _cp so the seed helpers stay grouped by semantic scope.
    private ControlPlaneDbContext _app => _cp;
    private PlatformAnalyticsService _sut = null!;
    private FakeTimeProvider _clock = null!;

    [SetUp]
    public void SetUp()
    {
        var cpOptions = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase($"cp-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _cp = new ControlPlaneDbContext(cpOptions);

        _clock = new FakeTimeProvider(FixedNow);
        _sut = new PlatformAnalyticsService(_cp, _clock);
    }

    [TearDown]
    public void TearDown()
    {
        _cp.Dispose();
    }

    // ── TenantCounts ──

    [Test]
    public async Task GetSummary_TenantCounts_PartitionsByStatus()
    {
        await SeedTenantAsync("alpha", status: "active");
        await SeedTenantAsync("beta", status: "active");
        await SeedTenantAsync("gamma", status: "provisioning");
        await SeedTenantAsync("delta", status: null);
        await SeedTenantAsync("epsilon", status: "active", softDeleted: true);

        var summary = await _sut.GetSummaryAsync();

        summary.Tenants.Active.Should().Be(2);
        summary.Tenants.Provisioning.Should().Be(1);
        summary.Tenants.Deleted.Should().Be(1);
        summary.Tenants.Total.Should().Be(4);
    }

    [Test]
    public async Task GetSummary_TenantCounts_DeletingStatusCountsAsDeleted()
    {
        await SeedTenantAsync("alpha", status: "deleting");
        await SeedTenantAsync("beta", status: "deleted");
        await SeedTenantAsync("gamma", status: "active");

        var summary = await _sut.GetSummaryAsync();

        summary.Tenants.Active.Should().Be(1);
        summary.Tenants.Deleted.Should().Be(2);
        summary.Tenants.Total.Should().Be(1);
    }

    // ── WorkflowCounts ──

    [Test]
    public async Task GetSummary_WorkflowCounts_BucketsByWindow()
    {
        var tenantId = Guid.NewGuid();
        await SeedWorkflowAsync(tenantId, "completed", FixedNow.AddHours(-1));
        await SeedWorkflowAsync(tenantId, "failed", FixedNow.AddHours(-2));
        await SeedWorkflowAsync(tenantId, "running", FixedNow.AddHours(-3));
        await SeedWorkflowAsync(tenantId, "pending", FixedNow.AddHours(-4));
        await SeedWorkflowAsync(tenantId, "completed", FixedNow.AddDays(-5));
        await SeedWorkflowAsync(tenantId, "failed", FixedNow.AddDays(-25));
        await SeedWorkflowAsync(tenantId, "completed", FixedNow.AddDays(-40));

        var summary = await _sut.GetSummaryAsync();

        summary.Workflows.Last24h.Completed.Should().Be(1);
        summary.Workflows.Last24h.Failed.Should().Be(1);
        summary.Workflows.Last24h.Running.Should().Be(2);
        summary.Workflows.Last7d.Completed.Should().Be(2);
        summary.Workflows.Last7d.Failed.Should().Be(1);
        summary.Workflows.Last30d.Completed.Should().Be(2);
        summary.Workflows.Last30d.Failed.Should().Be(2);
    }

    // ── AgentDispatch ──

    [Test]
    public async Task GetSummary_AgentDispatchCounts_FiltersByPrefix()
    {
        await SeedPlatformEventAsync("AGENT.DISPATCH.SUCCESS", FixedNow.AddMinutes(-10));
        await SeedPlatformEventAsync("AGENT.DISPATCH.SUCCESS", FixedNow.AddMinutes(-30));
        await SeedPlatformEventAsync("AGENT.DISPATCH.FAILED", FixedNow.AddHours(-2));
        await SeedPlatformEventAsync("AGENT.DISPATCH.STARTED", FixedNow.AddHours(-3));
        await SeedPlatformEventAsync("AGENT.DISPATCH.SUCCESS", FixedNow.AddDays(-2));
        await SeedPlatformEventAsync("TENANT.CREATED.SUCCESS", FixedNow.AddMinutes(-5));

        var summary = await _sut.GetSummaryAsync();

        summary.AgentDispatches.Last24h.Attempted.Should().Be(4);
        summary.AgentDispatches.Last24h.Success.Should().Be(2);
        summary.AgentDispatches.Last24h.Failed.Should().Be(1);
        summary.AgentDispatches.Last7d.Attempted.Should().Be(5);
        summary.AgentDispatches.Last7d.Success.Should().Be(3);
    }

    // ── CostAggregates ──

    [Test]
    public async Task GetSummary_CostAggregates_SumsLlmCostUsd()
    {
        var t = Guid.NewGuid();
        await SeedDomainEventAsync("LLM.CALL.SUCCESS", FixedNow.AddHours(-1),
            "{\"costUsd\":0.0125,\"inputTokens\":100}", tenantId: t);
        await SeedDomainEventAsync("LLM.CALL.SUCCESS", FixedNow.AddHours(-10),
            "{\"costUsd\":0.0250}", tenantId: t);
        await SeedDomainEventAsync("LLM.CALL.SUCCESS", FixedNow.AddDays(-5),
            "{\"costUsd\":1.0000}", tenantId: t);
        await SeedDomainEventAsync("LLM.CALL.SUCCESS", FixedNow.AddHours(-2),
            "not json", tenantId: t);
        await SeedDomainEventAsync("LLM.CALL.SUCCESS", FixedNow.AddHours(-2),
            "{\"inputTokens\":50}", tenantId: t);
        await SeedDomainEventAsync("TENANT.ACCESSED", FixedNow.AddHours(-1),
            "{\"costUsd\":99.9}", tenantId: t);

        var summary = await _sut.GetSummaryAsync();

        summary.Costs.Last24hUsd.Should().Be(0.0375m);
        summary.Costs.Last7dUsd.Should().Be(1.0375m);
        summary.Costs.Last30dUsd.Should().Be(1.0375m);
    }

    [Test]
    public async Task GetSummary_CostAggregates_AcceptsStringCostUsd()
    {
        var t = Guid.NewGuid();
        await SeedDomainEventAsync("LLM.CALL.SUCCESS", FixedNow.AddHours(-1),
            "{\"costUsd\":\"0.1234\"}", tenantId: t);

        var summary = await _sut.GetSummaryAsync();
        summary.Costs.Last24hUsd.Should().Be(0.1234m);
    }

    [Test]
    public void TryExtractCostUsd_RejectsMalformedJson()
    {
        var parsed = PlatformAnalyticsService.TryExtractCostUsd("not json", out var cost);
        parsed.Should().BeFalse();
        cost.Should().Be(0m);
    }

    [Test]
    public void TryExtractCostUsd_AcceptsNumber()
    {
        var parsed = PlatformAnalyticsService.TryExtractCostUsd(
            "{\"costUsd\":0.5}", out var cost);
        parsed.Should().BeTrue();
        cost.Should().Be(0.5m);
    }

    // ── GetTopTenants ──

    [Test]
    public async Task GetTopTenants_SortsByWorkflowVolumeDesc()
    {
        var alpha = await SeedTenantAsync("alpha", status: "active");
        var beta = await SeedTenantAsync("beta", status: "active");
        var gamma = await SeedTenantAsync("gamma", status: "active");

        for (var i = 0; i < 5; i++)
            await SeedWorkflowAsync(alpha, "completed", FixedNow.AddHours(-i));
        for (var i = 0; i < 10; i++)
            await SeedWorkflowAsync(beta, "completed", FixedNow.AddHours(-i));
        for (var i = 0; i < 3; i++)
            await SeedWorkflowAsync(gamma, "completed", FixedNow.AddHours(-i));

        await SeedDomainEventAsync("LLM.CALL.SUCCESS", FixedNow.AddHours(-1),
            "{\"costUsd\":2.50}", tenantId: beta);

        var rows = await _sut.GetTopTenantsAsync();

        rows.Should().HaveCount(3);
        rows[0].TenantId.Should().Be(beta);
        rows[0].WorkflowsLast30d.Should().Be(10);
        rows[0].CostUsdLast30d.Should().Be(2.50m);
        rows[1].TenantId.Should().Be(alpha);
        rows[1].WorkflowsLast30d.Should().Be(5);
        rows[2].TenantId.Should().Be(gamma);
    }

    [Test]
    public async Task GetTopTenants_OmitsEmptyTenants()
    {
        var alpha = await SeedTenantAsync("alpha", status: "active");
        await SeedTenantAsync("beta", status: "active");
        await SeedWorkflowAsync(alpha, "completed", FixedNow.AddHours(-1));

        var rows = await _sut.GetTopTenantsAsync();

        rows.Should().ContainSingle().Which.TenantId.Should().Be(alpha);
    }

    [Test]
    public async Task GetTopTenants_ClampsLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            var t = await SeedTenantAsync($"tenant-{i}", status: "active");
            await SeedWorkflowAsync(t, "completed", FixedNow.AddHours(-1));
        }
        var rows = await _sut.GetTopTenantsAsync(limit: -1);
        rows.Should().ContainSingle();
    }

    [Test]
    public async Task GetTopTenants_IncludesFailedCounts()
    {
        var alpha = await SeedTenantAsync("alpha", status: "active");
        await SeedWorkflowAsync(alpha, "completed", FixedNow.AddHours(-1));
        await SeedWorkflowAsync(alpha, "completed", FixedNow.AddHours(-2));
        await SeedWorkflowAsync(alpha, "failed", FixedNow.AddHours(-3));

        var rows = await _sut.GetTopTenantsAsync();

        rows.Should().ContainSingle();
        rows[0].WorkflowsLast30d.Should().Be(3);
        rows[0].WorkflowsFailedLast30d.Should().Be(1);
    }

    // ── GetEventHistogram ──

    [Test]
    public async Task GetEventHistogram_GroupsByTypeAndSortsDesc()
    {
        await SeedPlatformEventAsync("TENANT.CREATED.SUCCESS", FixedNow.AddHours(-1));
        await SeedPlatformEventAsync("TENANT.CREATED.SUCCESS", FixedNow.AddHours(-2));
        await SeedPlatformEventAsync("TENANT.CREATED.SUCCESS", FixedNow.AddHours(-3));
        await SeedPlatformEventAsync("USER.LOGIN.SUCCESS", FixedNow.AddHours(-1));
        await SeedPlatformEventAsync("USER.LOGIN.SUCCESS", FixedNow.AddHours(-2));
        await SeedPlatformEventAsync("AGENT.DISPATCH.SUCCESS", FixedNow.AddHours(-1));
        await SeedPlatformEventAsync("TENANT.CREATED.SUCCESS", FixedNow.AddDays(-3));

        var buckets = await _sut.GetEventHistogramAsync();

        buckets.Should().HaveCount(3);
        buckets[0].Type.Should().Be("TENANT.CREATED.SUCCESS");
        buckets[0].Count.Should().Be(3);
        buckets[1].Type.Should().Be("USER.LOGIN.SUCCESS");
        buckets[1].Count.Should().Be(2);
        buckets[2].Type.Should().Be("AGENT.DISPATCH.SUCCESS");
        buckets[2].Count.Should().Be(1);
    }

    [Test]
    public async Task GetEventHistogram_ExplicitSinceOverridesDefault()
    {
        await SeedPlatformEventAsync("TENANT.CREATED.SUCCESS", FixedNow.AddDays(-3));
        await SeedPlatformEventAsync("TENANT.CREATED.SUCCESS", FixedNow.AddDays(-10));
        await SeedPlatformEventAsync("TENANT.CREATED.SUCCESS", FixedNow.AddDays(-40));

        var buckets = await _sut.GetEventHistogramAsync(since: FixedNow.AddDays(-15));

        buckets.Should().ContainSingle();
        buckets[0].Count.Should().Be(2);
    }

    [Test]
    public async Task GetEventHistogram_ClampsLimitAtUpperBound()
    {
        for (var i = 0; i < 110; i++)
            await SeedPlatformEventAsync($"TYPE.{i:D3}", FixedNow.AddMinutes(-i));

        var buckets = await _sut.GetEventHistogramAsync(limit: 500);
        buckets.Should().HaveCount(100);
    }

    [Test]
    public async Task GetSummary_StampsGeneratedAtFromClock()
    {
        var summary = await _sut.GetSummaryAsync();
        summary.GeneratedAt.Should().Be(FixedNow);
    }

    // ── Seed helpers ──

    private async Task<Guid> SeedTenantAsync(string slug, string? status, bool softDeleted = false)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = slug,
            Slug = slug,
            Type = "personal",
            Plan = "free",
            CreatedAt = FixedNow.AddDays(-60),
            UpdatedAt = FixedNow,
            DeletedAt = softDeleted ? FixedNow.AddMinutes(-5) : null,
        };
        _cp.Tenants.Add(tenant);
        if (status is not null)
            _cp.Entry(tenant).Property("Status").CurrentValue = status;
        await _cp.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task SeedWorkflowAsync(Guid tenantId, string status, DateTime createdAt)
    {
        _app.WorkflowInstances.Add(new WorkflowInstance
        {
            Id = Guid.NewGuid(),
            DefinitionId = Guid.NewGuid(),
            TenantId = tenantId,
            Status = status,
            Variables = "{}",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await _app.SaveChangesAsync();
    }

    private async Task SeedPlatformEventAsync(string type, DateTime createdAt)
    {
        _cp.PlatformEvents.Add(new PlatformEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            Tags = "{}",
            Metadata = "{\"eventSource\":\"system\"}",
            Data = "{}",
            CreatedAt = createdAt,
        });
        await _cp.SaveChangesAsync();
    }

    private async Task SeedDomainEventAsync(string type, DateTime createdAt, string data, Guid? tenantId = null)
    {
        _app.DomainEvents.Add(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            TenantId = tenantId,
            Tags = "{}",
            Metadata = "{\"eventSource\":\"system\"}",
            Data = data,
            CreatedAt = createdAt,
        });
        await _app.SaveChangesAsync();
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTime _utcNow;
        public FakeTimeProvider(DateTime utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => new(_utcNow, TimeSpan.Zero);
    }
}
