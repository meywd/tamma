using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Provisioning;

/// <summary>
/// Tests for <see cref="NullTenantProvisioner"/>. Verifies the dev / shared-
/// infrastructure fallback flips state to Ready immediately without making
/// any external calls.
/// </summary>
[TestFixture]
public class NullTenantProvisionerTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032
    private TammaDbContext _db = null!;
#pragma warning restore NUnit1032
    private NullTenantProvisioner _provisioner = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        _provisioner = new NullTenantProvisioner(
            _db, NullLogger<NullTenantProvisioner>.Instance);
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    private async Task<Tenant> SeedAsync()
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            Slug = "acme-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            ProvisioningState = "none",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();
        return tenant;
    }

    private async Task<Tenant> ReloadAsync(Guid tenantId)
    {
        foreach (var entry in _db.ChangeTracker.Entries<Tenant>().ToList())
            entry.State = EntityState.Detached;
        return await _db.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == tenantId);
    }

    [Test]
    public async Task ProvisionAsync_ImmediatelyMarksReady_NoCranlColumnsSet()
    {
        var tenant = await SeedAsync();
        var status = await _provisioner.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        status.State.Should().Be(ProvisioningState.Ready);

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("ready");
        refreshed.ProvisioningDetail.Should().Be("shared_infrastructure_no_cranl_configured");
        // Cranl columns stay null — tenant rides on the central DB.
        refreshed.CranlProjectId.Should().BeNull();
        refreshed.CranlDatabaseId.Should().BeNull();
        refreshed.CranlAppId.Should().BeNull();
        refreshed.CranlDatabaseUrlEncrypted.Should().BeNull();
    }

    [Test]
    public async Task GetStatusAsync_ReturnsCurrentRowState()
    {
        var tenant = await SeedAsync();
        var status = await _provisioner.GetStatusAsync(tenant.Id, CancellationToken.None);
        status.State.Should().Be(ProvisioningState.None);
    }

    [Test]
    public async Task DeprovisionAsync_FlipsToDeprovisioned()
    {
        var tenant = await SeedAsync();
        tenant.ProvisioningState = "ready";
        await _db.SaveChangesAsync();

        await _provisioner.DeprovisionAsync(tenant.Id, CancellationToken.None);

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("deprovisioned");
    }
}
