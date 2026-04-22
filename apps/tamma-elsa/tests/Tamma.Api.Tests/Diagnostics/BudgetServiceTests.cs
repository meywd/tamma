using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using BudgetConfig = Tamma.Api.Services.Diagnostics.Models.BudgetConfig;

namespace Tamma.Api.Tests.Diagnostics;

/// <summary>
/// Tests for per-account budget aggregation and threshold alerting.
/// </summary>
[TestFixture]
public class BudgetServiceTests
{
    private IServiceScope _scope = null!;
    // DbContext is owned by _scope; disposing the scope cascades.
#pragma warning disable NUnit1032
    private TammaDbContext _db = null!;
#pragma warning restore NUnit1032
    private IDiagnosticsRepository _repo = null!;
    private IDiagnosticsService _service = null!;
    private IBudgetConfigProvider _budgetProvider = null!;

    [SetUp]
    public async Task SetUp()
    {
        await DiagnosticsSetUpFixture.ResetDatabaseAsync();
        _scope = DiagnosticsTestHarness.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        _repo = _scope.ServiceProvider.GetRequiredService<IDiagnosticsRepository>();
        _service = _scope.ServiceProvider.GetRequiredService<IDiagnosticsService>();
        _budgetProvider = _scope.ServiceProvider.GetRequiredService<IBudgetConfigProvider>();
    }

    [TearDown]
    public void TearDown() => _scope.Dispose();

    // ──────────────────────────────────────────────────────────────────────
    // Cost sum
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetCostSumAsync_SumsCostWithinRange()
    {
        var tenant = Guid.NewGuid();
        var now = new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);

        await SeedAsync(tenant, now.AddHours(-2), cost: 1.50m);
        await SeedAsync(tenant, now.AddHours(-1), cost: 2.75m);
        await SeedAsync(tenant, now.AddHours(-5), cost: 999m); // outside

        var sum = await _repo.GetCostSumAsync(tenant, now.AddHours(-3), now);
        sum.Should().Be(4.25m);
    }

    [Test]
    public async Task GetCostSumAsync_NoMatchingRows_ReturnsZero()
    {
        var tenant = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var sum = await _repo.GetCostSumAsync(tenant, now.AddHours(-1), now);
        sum.Should().Be(0m);
    }

    [Test]
    public async Task GetCostSumAsync_IsolatesByTenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await SeedAsync(tenantA, now.AddMinutes(-5), cost: 10m);
        await SeedAsync(tenantB, now.AddMinutes(-5), cost: 100m);

        var sumA = await _repo.GetCostSumAsync(tenantA, now.AddHours(-1), now);
        sumA.Should().Be(10m);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Budget thresholds
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetBudgetAsync_BelowThreshold_DoesNotAlert()
    {
        var tenant = Guid.NewGuid();
        var now = DateTime.UtcNow;
        _budgetProvider.SetConfig(tenant, new BudgetConfig(
            LimitUsd: 100m,
            AlertThreshold: 0.8,
            PeriodStart: now.AddDays(-15),
            PeriodEnd: now.AddDays(15)));

        await SeedAsync(tenant, now, cost: 50m);

        var status = await _service.GetBudgetAsync(tenant);

        status.Spent.Should().Be(50m);
        status.Limit.Should().Be(100m);
        status.Remaining.Should().Be(50m);
        status.PercentUsed.Should().BeApproximately(50.0, 0.0001);
        status.ShouldAlert.Should().BeFalse();
        status.IsOverBudget.Should().BeFalse();
    }

    [Test]
    public async Task GetBudgetAsync_AtThreshold_Alerts()
    {
        var tenant = Guid.NewGuid();
        var now = DateTime.UtcNow;
        _budgetProvider.SetConfig(tenant, new BudgetConfig(
            LimitUsd: 100m,
            AlertThreshold: 0.8,
            PeriodStart: now.AddDays(-15),
            PeriodEnd: now.AddDays(15)));

        await SeedAsync(tenant, now, cost: 80m);

        var status = await _service.GetBudgetAsync(tenant);

        status.ShouldAlert.Should().BeTrue();
        status.IsOverBudget.Should().BeFalse();
    }

    [Test]
    public async Task GetBudgetAsync_OverBudget_FlagsAsOverBudget()
    {
        var tenant = Guid.NewGuid();
        var now = DateTime.UtcNow;
        _budgetProvider.SetConfig(tenant, new BudgetConfig(
            LimitUsd: 100m,
            AlertThreshold: 0.8,
            PeriodStart: now.AddDays(-15),
            PeriodEnd: now.AddDays(15)));

        await SeedAsync(tenant, now, cost: 120m);

        var status = await _service.GetBudgetAsync(tenant);

        status.Spent.Should().Be(120m);
        status.Remaining.Should().Be(0m); // floored at 0
        status.IsOverBudget.Should().BeTrue();
        status.ShouldAlert.Should().BeTrue();
    }

    [Test]
    public async Task GetBudgetAsync_ZeroLimit_PercentUsedIsZero()
    {
        var tenant = Guid.NewGuid();
        var now = DateTime.UtcNow;
        _budgetProvider.SetConfig(tenant, new BudgetConfig(
            LimitUsd: 0m,
            AlertThreshold: 0.8,
            PeriodStart: now.AddDays(-15),
            PeriodEnd: now.AddDays(15)));

        await SeedAsync(tenant, now, cost: 5m);

        var status = await _service.GetBudgetAsync(tenant);
        status.Limit.Should().Be(0m);
        status.PercentUsed.Should().Be(0);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Period boundaries
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetBudgetAsync_ExcludesSpendOutsidePeriod()
    {
        var tenant = Guid.NewGuid();
        var now = DateTime.UtcNow;
        _budgetProvider.SetConfig(tenant, new BudgetConfig(
            LimitUsd: 100m,
            AlertThreshold: 0.8,
            PeriodStart: now.AddDays(-5),
            PeriodEnd: now.AddDays(5)));

        // In-period spend
        await SeedAsync(tenant, now.AddDays(-1), cost: 30m);
        // Pre-period spend
        await SeedAsync(tenant, now.AddDays(-10), cost: 500m);

        var status = await _service.GetBudgetAsync(tenant);

        status.Spent.Should().Be(30m);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private async Task SeedAsync(Guid tenantId, DateTime createdAt, decimal cost, bool success = true)
    {
        // Phase-1 hardening (finding 032) added an FK on
        // provider_diagnostics.TenantId → tenants.Id with ON DELETE SET NULL.
        // Tests must materialise the tenant row before the diagnostic insert.
        await EnsureTenantAsync(tenantId);

        var row = new ProviderDiagnostic
        {
            Id = Guid.NewGuid(),
            ProviderKey = "anthropic-claude",
            TenantId = tenantId,
            Cost = cost,
            Success = success,
            CreatedAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)
        };
        _db.ProviderDiagnostics.Add(row);
        await _db.SaveChangesAsync();
    }

    private async Task EnsureTenantAsync(Guid tenantId)
    {
        if (await _db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
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
