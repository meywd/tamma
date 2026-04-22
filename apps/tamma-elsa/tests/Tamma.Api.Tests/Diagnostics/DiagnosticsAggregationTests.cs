using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Diagnostics;

/// <summary>
/// Integration tests for the repository-level time-bucketing + the service
/// wrapper that turns rows into <see cref="DiagnosticsBucket"/> DTOs.
/// </summary>
[TestFixture]
public class DiagnosticsAggregationTests
{
    private IServiceScope _scope = null!;
    private IDiagnosticsRepository _repo = null!;
    private IDiagnosticsService _service = null!;
    // DbContext is owned by _scope; disposing the scope cascades correctly.
    // Suppress NUnit1032 (false positive for scoped deps).
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032

    [SetUp]
    public async Task SetUp()
    {
        await DiagnosticsSetUpFixture.ResetDatabaseAsync();
        _scope = DiagnosticsTestHarness.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        _repo = _scope.ServiceProvider.GetRequiredService<IDiagnosticsRepository>();
        _service = _scope.ServiceProvider.GetRequiredService<IDiagnosticsService>();
    }

    [TearDown]
    public void TearDown() => _scope.Dispose();

    // ──────────────────────────────────────────────────────────────────────
    // Bucket size — 5 minute
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AggregateAsync_FiveMinuteBuckets_GroupsRowsIntoCorrectBuckets()
    {
        var tenant = Guid.NewGuid();
        var baseTime = new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);

        // Bucket 1: [12:00, 12:05) — 2 rows
        await SeedAsync(tenant, baseTime.AddMinutes(1), providerKey: "p1", cost: 1.00m, durationMs: 100, success: true);
        await SeedAsync(tenant, baseTime.AddMinutes(3), providerKey: "p1", cost: 2.00m, durationMs: 200, success: true);

        // Bucket 2: [12:05, 12:10) — 1 row (failed)
        await SeedAsync(tenant, baseTime.AddMinutes(6), providerKey: "p1", cost: 0.50m, durationMs: 50, success: false);

        // Outside range
        await SeedAsync(tenant, baseTime.AddHours(2), providerKey: "p1", cost: 99m, durationMs: 999, success: true);

        var buckets = await _repo.AggregateAsync(
            baseTime, baseTime.AddMinutes(15), TimeSpan.FromMinutes(5), tenant);

        buckets.Should().HaveCount(2);

        var b1 = buckets.Single(b => b.BucketStart == baseTime);
        b1.TotalCalls.Should().Be(2);
        b1.SuccessCount.Should().Be(2);
        b1.TotalCost.Should().Be(3.00m);
        b1.AvgLatencyMs.Should().BeApproximately(150.0, 0.01);

        var b2 = buckets.Single(b => b.BucketStart == baseTime.AddMinutes(5));
        b2.TotalCalls.Should().Be(1);
        b2.SuccessCount.Should().Be(0);
        b2.TotalCost.Should().Be(0.50m);
        b2.AvgLatencyMs.Should().BeApproximately(50.0, 0.01);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Bucket size — hour
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AggregateAsync_HourBuckets_GroupsByHour()
    {
        var tenant = Guid.NewGuid();
        var baseTime = new DateTime(2026, 4, 16, 0, 0, 0, DateTimeKind.Utc);

        await SeedAsync(tenant, baseTime.AddMinutes(15), cost: 1m, durationMs: 100, success: true);
        await SeedAsync(tenant, baseTime.AddMinutes(45), cost: 1m, durationMs: 100, success: true);
        await SeedAsync(tenant, baseTime.AddHours(1).AddMinutes(15), cost: 2m, durationMs: 200, success: false);

        var buckets = await _repo.AggregateAsync(
            baseTime, baseTime.AddHours(3), TimeSpan.FromHours(1), tenant);

        buckets.Should().HaveCount(2);
        buckets.Should().ContainSingle(b => b.BucketStart == baseTime && b.TotalCalls == 2);
        buckets.Should().ContainSingle(b => b.BucketStart == baseTime.AddHours(1) && b.TotalCalls == 1);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Bucket size — day
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AggregateAsync_DayBuckets_GroupsByDay()
    {
        var tenant = Guid.NewGuid();
        var day1 = new DateTime(2026, 4, 16, 0, 0, 0, DateTimeKind.Utc);
        var day2 = day1.AddDays(1);

        await SeedAsync(tenant, day1.AddHours(3), cost: 1m, success: true);
        await SeedAsync(tenant, day1.AddHours(20), cost: 1m, success: true);
        await SeedAsync(tenant, day2.AddHours(5), cost: 1m, success: false);

        var buckets = await _repo.AggregateAsync(
            day1, day1.AddDays(3), TimeSpan.FromDays(1), tenant);

        buckets.Should().HaveCount(2);
        buckets.Single(b => b.BucketStart == day1).TotalCalls.Should().Be(2);
        buckets.Single(b => b.BucketStart == day2).TotalCalls.Should().Be(1);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Empty range
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AggregateAsync_EmptyRange_ReturnsEmptyList()
    {
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant, DateTime.UtcNow, cost: 1m, success: true);

        var from = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var buckets = await _repo.AggregateAsync(from, from.AddHours(1), TimeSpan.FromMinutes(5), tenant);

        buckets.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────────────────────────────
    // Multi-tenant isolation
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AggregateAsync_IsolatesByTenantId()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var baseTime = new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);

        await SeedAsync(tenantA, baseTime.AddMinutes(1), cost: 10m, success: true);
        await SeedAsync(tenantA, baseTime.AddMinutes(2), cost: 20m, success: true);
        await SeedAsync(tenantB, baseTime.AddMinutes(1), cost: 999m, success: true);

        var bucketsA = await _repo.AggregateAsync(
            baseTime, baseTime.AddMinutes(5), TimeSpan.FromMinutes(5), tenantA);

        bucketsA.Should().HaveCount(1);
        bucketsA[0].TotalCalls.Should().Be(2);
        bucketsA[0].TotalCost.Should().Be(30m);
    }

    // ──────────────────────────────────────────────────────────────────────
    // DiagnosticsService.GetReportAsync (higher-level wrapper)
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetReportAsync_ComputesOverallTotalsAndSuccessRate()
    {
        var tenant = Guid.NewGuid();
        var baseTime = new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);

        await SeedAsync(tenant, baseTime.AddMinutes(1), cost: 1m, durationMs: 100, success: true);
        await SeedAsync(tenant, baseTime.AddMinutes(6), cost: 2m, durationMs: 200, success: true);
        await SeedAsync(tenant, baseTime.AddMinutes(11), cost: 4m, durationMs: 300, success: false);

        var report = await _service.GetReportAsync(
            tenant, baseTime, baseTime.AddMinutes(30), BucketSize.FiveMinutes);

        report.From.Should().Be(baseTime);
        report.To.Should().Be(baseTime.AddMinutes(30));
        report.BucketSize.Should().Be(BucketSize.FiveMinutes);
        report.Buckets.Should().HaveCount(3);
        report.TotalCalls.Should().Be(3);
        report.TotalCost.Should().Be(7m);
        report.OverallSuccessRate.Should().BeApproximately(2.0 / 3.0, 0.0001);
    }

    [Test]
    public async Task GetReportAsync_EmptyRange_ReturnsZeroesAndEmptyBuckets()
    {
        var tenant = Guid.NewGuid();
        var from = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var report = await _service.GetReportAsync(
            tenant, from, from.AddHours(1), BucketSize.FiveMinutes);

        report.Buckets.Should().BeEmpty();
        report.TotalCalls.Should().Be(0);
        report.TotalCost.Should().Be(0m);
        report.OverallSuccessRate.Should().Be(0);
    }

    [Test]
    public async Task GetReportAsync_SuccessRatePerBucket()
    {
        var tenant = Guid.NewGuid();
        var baseTime = new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);

        // Bucket 1 — 3 calls, 2 successful → 2/3
        await SeedAsync(tenant, baseTime.AddMinutes(1), success: true);
        await SeedAsync(tenant, baseTime.AddMinutes(2), success: true);
        await SeedAsync(tenant, baseTime.AddMinutes(3), success: false);

        var report = await _service.GetReportAsync(
            tenant, baseTime, baseTime.AddMinutes(5), BucketSize.FiveMinutes);

        report.Buckets.Should().HaveCount(1);
        report.Buckets[0].SuccessRate.Should().BeApproximately(2.0 / 3.0, 0.0001);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private async Task SeedAsync(
        Guid tenantId,
        DateTime createdAt,
        string providerKey = "anthropic-claude",
        decimal cost = 0m,
        double durationMs = 0,
        bool success = true,
        int tokensUsed = 0)
    {
        // Phase-1 hardening (finding 032) added an FK on TenantId → tenants.Id.
        // Materialise the tenant before the diagnostic insert.
        await EnsureTenantAsync(tenantId);

        // Bypass global query filter — insert raw entity.
        var row = new ProviderDiagnostic
        {
            Id = Guid.NewGuid(),
            ProviderKey = providerKey,
            TenantId = tenantId,
            Cost = cost,
            RequestDurationMs = durationMs,
            TokensUsed = tokensUsed,
            Success = success,
            CreatedAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)
        };
        _db.ProviderDiagnostics.Add(row);
        await _db.SaveChangesAsync();
    }

    private async Task EnsureTenantAsync(Guid tenantId)
    {
        if (await _db.Tenants.IgnoreQueryFilters()
                .AnyAsync(t => t.Id == tenantId))
            return;
        _db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = $"Test {tenantId:N}",
            Slug = $"t-{tenantId:N}",
            Plan = "free"
        });
        await _db.SaveChangesAsync();
    }
}
