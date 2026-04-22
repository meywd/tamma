using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Services.Diagnostics;
using Tamma.Data;
using Tamma.Data.Repositories;
using BudgetEntity = Tamma.Data.Entities.BudgetConfig;

namespace Tamma.Api.Tests.Diagnostics;

/// <summary>
/// Integration tests for <see cref="BudgetConfigRepository"/> and the
/// <see cref="PostgresBudgetConfigProvider"/> (audit finding providers/005
/// persistence follow-up). Exercises upsert / read / delete against the
/// real Postgres test container.
/// </summary>
[TestFixture]
public class BudgetConfigRepositoryTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032
    private IBudgetConfigRepository _repo = null!;

    [SetUp]
    public async Task SetUp()
    {
        await DiagnosticsSetUpFixture.ResetDatabaseAsync();
        _scope = DiagnosticsTestHarness.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        _repo = _scope.ServiceProvider.GetRequiredService<IBudgetConfigRepository>();
    }

    [TearDown]
    public void TearDown() => _scope.Dispose();

    [Test]
    public async Task Upsert_InsertsNewRow_WhenNoneExists()
    {
        var tenant = Guid.NewGuid();
        var saved = await _repo.UpsertAsync(new BudgetEntity
        {
            TenantId = tenant,
            AccountId = tenant.ToString(),
            LimitUsd = 42m,
            AlertThreshold = 0.75,
            PeriodDays = 7,
        });

        saved.Id.Should().NotBe(Guid.Empty);
        saved.LimitUsd.Should().Be(42m);
        saved.AlertThreshold.Should().Be(0.75);
        saved.PeriodDays.Should().Be(7);
    }

    [Test]
    public async Task Upsert_UpdatesExistingRow_WhenKeyMatches()
    {
        var tenant = Guid.NewGuid();
        var account = tenant.ToString();

        await _repo.UpsertAsync(new BudgetEntity
        {
            TenantId = tenant, AccountId = account, LimitUsd = 10m,
        });
        var updated = await _repo.UpsertAsync(new BudgetEntity
        {
            TenantId = tenant, AccountId = account, LimitUsd = 200m,
            AlertThreshold = 0.5, PeriodDays = 60,
        });

        updated.LimitUsd.Should().Be(200m);
        updated.AlertThreshold.Should().Be(0.5);
        updated.PeriodDays.Should().Be(60);

        var reread = await _repo.GetAsync(tenant, account);
        reread.Should().NotBeNull();
        reread!.LimitUsd.Should().Be(200m);
    }

    [Test]
    public async Task Get_ReturnsNull_WhenRowAbsent()
    {
        var tenant = Guid.NewGuid();
        var row = await _repo.GetAsync(tenant, tenant.ToString());
        row.Should().BeNull();
    }

    [Test]
    public async Task Delete_ReturnsTrue_WhenRowExists()
    {
        var tenant = Guid.NewGuid();
        var account = tenant.ToString();
        await _repo.UpsertAsync(new BudgetEntity
        {
            TenantId = tenant, AccountId = account, LimitUsd = 1m,
        });

        var removed = await _repo.DeleteAsync(tenant, account);
        removed.Should().BeTrue();

        var reread = await _repo.GetAsync(tenant, account);
        reread.Should().BeNull();
    }

    [Test]
    public async Task Delete_ReturnsFalse_WhenRowAbsent()
    {
        var removed = await _repo.DeleteAsync(Guid.NewGuid(), "nonexistent");
        removed.Should().BeFalse();
    }

    [Test]
    public async Task DefaultRow_CanCoexistWithTenantRow()
    {
        // TenantId = null is the platform default; it should coexist with a
        // tenant-specific override on the same accountId. Exercises the
        // partial-unique-index split.
        var tenant = Guid.NewGuid();
        var account = tenant.ToString();

        await _repo.UpsertAsync(new BudgetEntity
        {
            TenantId = null, AccountId = account, LimitUsd = 50m,
        });
        await _repo.UpsertAsync(new BudgetEntity
        {
            TenantId = tenant, AccountId = account, LimitUsd = 999m,
        });

        var defaultRow = await _repo.GetAsync(null, account);
        var tenantRow = await _repo.GetAsync(tenant, account);

        defaultRow.Should().NotBeNull();
        defaultRow!.LimitUsd.Should().Be(50m);
        tenantRow.Should().NotBeNull();
        tenantRow!.LimitUsd.Should().Be(999m);
    }
}

/// <summary>
/// Exercises <see cref="PostgresBudgetConfigProvider"/> end-to-end against
/// the test container: SetConfig writes a DB row; GetConfig returns the
/// same values; freshly-resolved providers see the persisted state.
/// </summary>
[TestFixture]
public class PostgresBudgetConfigProviderTests
{
    [SetUp]
    public async Task SetUp()
    {
        await DiagnosticsSetUpFixture.ResetDatabaseAsync();
    }

    [Test]
    public void Provider_IsPostgresImpl_InProductionDI()
    {
        using var scope = DiagnosticsTestHarness.CreateScope();
        var prod = scope.ServiceProvider.GetRequiredService<IBudgetConfigProvider>();
        prod.Should().BeOfType<PostgresBudgetConfigProvider>();
    }

    [Test]
    public void GetConfig_ReturnsDefault_WhenNoRowExists()
    {
        using var scope = DiagnosticsTestHarness.CreateScope();
        var pg = scope.ServiceProvider.GetRequiredService<IBudgetConfigProvider>();

        var cfg = pg.GetConfig(Guid.NewGuid());
        // Default is 0m when Budget:LimitUsd isn't configured in tests.
        cfg.LimitUsd.Should().Be(0m);
    }

    [Test]
    public void SetConfig_PersistsToDb_AndGetConfigRoundTrips()
    {
        var account = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // First scope — write.
        using (var scope1 = DiagnosticsTestHarness.CreateScope())
        {
            var pg = scope1.ServiceProvider.GetRequiredService<IBudgetConfigProvider>();
            pg.SetConfig(account, new Tamma.Api.Services.Diagnostics.Models.BudgetConfig(
                LimitUsd: 321.5m,
                AlertThreshold: 0.42,
                PeriodStart: now.AddDays(-10),
                PeriodEnd: now.AddDays(10)));
        }

        // Second scope — read. The harness's singleton cache is shared, but
        // the row is now in Postgres either way; this verifies round-trip.
        using (var scope2 = DiagnosticsTestHarness.CreateScope())
        {
            var pg = scope2.ServiceProvider.GetRequiredService<IBudgetConfigProvider>();
            var cfg = pg.GetConfig(account);

            cfg.LimitUsd.Should().Be(321.5m);
            cfg.AlertThreshold.Should().Be(0.42);
        }
    }
}
